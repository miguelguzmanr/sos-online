using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;

namespace SosOnline;

internal static class Il2CppUtil
{
    /// <summary>Copies an IL2CPP IList&lt;T&gt; (whose Count lives on ICollection&lt;T&gt;) into a managed list.</summary>
    public static List<T> ToList<T>(this Il2CppSystem.Collections.Generic.IList<T> list) where T : Il2CppObjectBase
    {
        var result = new List<T>();
        if (list == null) return result;
        int n = list.Cast<Il2CppSystem.Collections.Generic.ICollection<T>>().Count;
        for (int i = 0; i < n; i++) result.Add(list[i]);
        return result;
    }
}
