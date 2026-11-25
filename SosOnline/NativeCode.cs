using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace SosOnline;

/// <summary>
/// IL2CPP builds fold identical native functions (empty virtuals, trivial getters/setters, shared generic code) into
/// one. Detouring such a function also hooks every other method sharing it, with unrelated `this` objects.
///
/// Two defenses:
///  1. <see cref="AuditPatches"/>: maps the native code of every method of every class the IL2CPP runtime knows and
///     removes our patches that land on shared code.
///  2. <see cref="Is{T}"/>: every instance patch checks the real class of `this` before doing anything, which also
///     covers sharing with generic instantiations (not enumerable from metadata).
/// </summary>
internal static unsafe class NativeCode
{
    private static Dictionary<IntPtr, List<string>> _byPointer;
    private static long[] _sortedPtrs;
    private static readonly List<(long start, long end)> _textSections = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll")]
    private static extern UIntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);

    private static string Str(nint p) => p == 0 ? "" : Marshal.PtrToStringAnsi(p) ?? "";

    private static void Build()
    {
        if (_byPointer != null) return;
        _byPointer = new Dictionary<IntPtr, List<string>>();
        _textSections.Clear();
        var module = GetModuleHandle("GameAssembly.dll");
        if (module != IntPtr.Zero)
        {
            long baseAddr = module.ToInt64();
            int pe = *(int*)(baseAddr + 0x3C);
            short numSec = *(short*)(baseAddr + pe + 6);
            short optSize = *(short*)(baseAddr + pe + 20);
            long secBase = baseAddr + pe + 24 + optSize;
            for (int s = 0; s < numSec; s++)
            {
                long sh = secBase + s * 40;
                uint chars = *(uint*)(sh + 36);
                if ((chars & 0x20000000) == 0) continue; // IMAGE_SCN_MEM_EXECUTE
                long sStart = baseAddr + *(uint*)(sh + 12);
                long sEnd = sStart + *(uint*)(sh + 8);
                _textSections.Add((sStart, sEnd));
            }
        }
        uint count = 0;
        var assemblies = IL2CPP.il2cpp_domain_get_assemblies(IL2CPP.il2cpp_domain_get(), ref count);
        for (uint a = 0; a < count; a++)
        {
            var image = IL2CPP.il2cpp_assembly_get_image(assemblies[a]);
            uint classes = IL2CPP.il2cpp_image_get_class_count(image);
            for (uint c = 0; c < classes; c++)
            {
                var klass = IL2CPP.il2cpp_image_get_class(image, c);
                if (klass == IntPtr.Zero) continue;
                string className = null;
                IntPtr iter = IntPtr.Zero;
                IntPtr method;
                while ((method = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                {
                    var code = *(IntPtr*)method; // MethodInfo.methodPointer
                    if (code == IntPtr.Zero) continue;
                    className ??= Str(IL2CPP.il2cpp_class_get_name(klass));
                    if (!_byPointer.TryGetValue(code, out var list)) _byPointer[code] = list = new List<string>(1);
                    list.Add($"{className}.{Str(IL2CPP.il2cpp_method_get_name(method))}");
                }
            }
        }
        _sortedPtrs = _byPointer.Keys.Select(p => p.ToInt64()).OrderBy(x => x).ToArray();
    }

    private static bool InTextSection(long addr)
    {
        for (int i = 0; i < _textSections.Count; i++)
        {
            var (s, e) = _textSections[i];
            if (addr >= s + 8 && addr < e) return true;
        }
        return false;
    }

    /// <summary>Walks committed stack slots from <paramref name="sp"/> to resolve GameAssembly.dll call return addresses.</summary>
    public static string CallerChainFromStack(long* sp, int maxSlots = 256, int maxFrames = 4)
    {
        Build();
        if (_sortedPtrs == null || _sortedPtrs.Length == 0 || _textSections.Count == 0) return "?";
        if (VirtualQuery((IntPtr)sp, out var mbi, (UIntPtr)sizeof(MEMORY_BASIC_INFORMATION)) == UIntPtr.Zero) return "?";
        long stackEnd = mbi.BaseAddress.ToInt64() + (long)mbi.RegionSize;
        int slots = (int)Math.Min(maxSlots, Math.Max(0, (stackEnd - (long)sp) / 8));
        var parts = new List<string>(maxFrames);
        for (int i = 0; i < slots && parts.Count < maxFrames; i++)
        {
            long addr = sp[i];
            if (!InTextSection(addr)) continue;
            byte* p = (byte*)addr;
            bool looksLikeCall = p[-5] == 0xE8 || p[-6] == 0xFF || p[-2] == 0xFF || p[-3] == 0xFF || p[-4] == 0xFF || p[-7] == 0xFF;
            if (!looksLikeCall) continue;
            int idx = Array.BinarySearch(_sortedPtrs, addr);
            if (idx < 0) idx = (~idx) - 1;
            if (idx < 0) continue;
            long fn = _sortedPtrs[idx];
            var names = _byPointer[(IntPtr)fn];
            string label = names.Count == 1 ? names[0] : $"{names[0]}(+{names.Count - 1})";
            if (parts.Count == 0 || parts[^1] != label) parts.Add(label);
        }
        return parts.Count > 0 ? string.Join(" <- ", parts) : "non-GameAssembly";
    }

    /// <summary>Native code pointer of an interop method, via its NativeMethodInfoPtr field.</summary>
    public static IntPtr CodeOf(MethodBase m)
    {
        try
        {
            var field = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(m);
            if (field == null) return IntPtr.Zero;
            var info = (IntPtr)field.GetValue(null);
            return info == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)info;
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>How many methods share this method's native code (1 = not folded).</summary>
    public static int SharedCount(MethodBase m, out List<string> sharers)
    {
        Build();
        sharers = null;
        var code = CodeOf(m);
        if (code == IntPtr.Zero) return 0;
        if (!_byPointer.TryGetValue(code, out sharers)) return 1;
        return sharers.Count;
    }

    public static bool IsFolded(MethodBase m) => SharedCount(m, out _) > 1;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);

    /// <summary>
    /// Diagnostics (--sosonline-whichrva=hex,...): the game methods whose native code starts nearest at or before
    /// GameAssembly.dll + <paramref name="rva"/>, e.g. the fault offset Windows reports for a crash.
    /// </summary>
    public static string Containing(long rva)
    {
        Build();
        var module = GetModuleHandle("GameAssembly.dll");
        if (module == IntPtr.Zero) return "GameAssembly.dll not loaded";
        long target = module.ToInt64() + rva;
        long best = 0;
        foreach (var p in _byPointer.Keys) { long v = p.ToInt64(); if (v <= target && v > best) best = v; }
        if (best == 0) return "none below it";
        long next = long.MaxValue;
        foreach (var p in _byPointer.Keys) { long v = p.ToInt64(); if (v > target && v < next) next = v; }
        return $"{string.Join(" / ", _byPointer[(IntPtr)best])} + 0x{target - best:x} (starts at rva 0x{best - module.ToInt64():x}; next method at rva 0x{(next == long.MaxValue ? 0 : next - module.ToInt64()):x})";
    }

    /// <summary>Method names whose native code starts at this address (empty if unknown).</summary>
    public static List<string> NamesAt(IntPtr code)
    {
        Build();
        return _byPointer.TryGetValue(code, out var l) ? l : new List<string>();
    }

    /// <summary>
    /// Checks every method this plugin patched; detours on native code shared with other methods are removed.
    /// </summary>
    public static void AuditPatches(Harmony harmony, string harmonyId)
    {
        int total = 0, folded = 0;
        foreach (var m in Harmony.GetAllPatchedMethods().ToList())
        {
            var info = Harmony.GetPatchInfo(m);
            if (info == null || !info.Owners.Contains(harmonyId)) continue;
            total++;
            int n = SharedCount(m, out var sharers);
            if (n > 1)
            {
                folded++;
                Plugin.Log.LogWarning($"[fold] {m.DeclaringType?.Name}.{m.Name} shares native code with {n - 1} other method(s) ({string.Join(", ", sharers.Take(4))}{(n > 5 ? ", …" : "")}); patch removed");
                harmony.Unpatch(m, HarmonyPatchType.All, harmonyId);
            }
        }
        Plugin.Log.LogInfo($"[fold] audited {total} patched methods against {_byPointer?.Count ?? 0} native functions, {folded} folded");
    }

    // ---------------------------------------------------------------- runtime class guard

    private static class ClassPtr<T> where T : Il2CppObjectBase
    {
        public static readonly IntPtr Value = Resolve();

        private static IntPtr Resolve()
        {
            RuntimeHelpers.RunClassConstructor(typeof(T).TypeHandle);
            return Il2CppClassPointerStore<T>.NativeClassPtr;
        }
    }

    /// <summary>True if the object really is a <typeparamref name="T"/> (not another class sharing the detoured code).</summary>
    public static bool Is<T>(Il2CppObjectBase obj) where T : Il2CppObjectBase
    {
        if (obj == null) return false;
        var ptr = obj.Pointer;
        if (ptr == IntPtr.Zero) return false;
        var target = ClassPtr<T>.Value;
        if (target == IntPtr.Zero) return false;
        var klass = IL2CPP.il2cpp_object_get_class(ptr);
        return klass != IntPtr.Zero && (klass == target || IL2CPP.il2cpp_class_is_assignable_from(target, klass));
    }
}
