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

    private static string Str(nint p) => p == 0 ? "" : Marshal.PtrToStringAnsi(p) ?? "";

    private static void Build()
    {
        if (_byPointer != null) return;
        _byPointer = new Dictionary<IntPtr, List<string>>();
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
