// tools: nx_api_lookup
// Search the NX Open .NET API that is loaded in this NX session, by reflection. Works offline.
using System;
using System.Collections.Generic;
using System.Reflection;

namespace NxAiBridge.Handlers
{
    public class ApiLookupTool : ITool
    {
        private static List<Type> cache;

        private static List<Type> NxTypes()
        {
            if (cache != null) return cache;
            List<Type> types = new List<Type>();
            ScriptCompiler.NxReferences(); // makes sure the standard NXOpen assemblies are loaded
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!asm.GetName().Name.StartsWith("NXOpen", StringComparison.OrdinalIgnoreCase)) continue;
                    types.AddRange(asm.GetExportedTypes());
                }
                catch (Exception) { }
            }
            cache = types;
            return types;
        }

        public object Run(string tool, Args a)
        {
            string typeName = a.Str("type", null);
            string query = a.Str("query", null);
            int limit = Math.Max(1, a.Int("limit", 60));
            if (string.IsNullOrEmpty(typeName) && string.IsNullOrEmpty(query))
                throw new ToolError("INVALID_PARAMS", "Give `type` and/or `query`.");
            List<Type> types = NxTypes();
            if (types.Count == 0) throw new ToolError("NOT_AVAILABLE", "No NXOpen assemblies are loaded in this process.");

            if (!string.IsNullOrEmpty(typeName))
            {
                string wanted = typeName.Replace('+', '.');
                List<Type> matches = new List<Type>();
                foreach (Type t in types)
                    if (string.Equals(Dyn.TypeName(t), wanted, StringComparison.OrdinalIgnoreCase)) matches.Add(t);
                if (matches.Count == 0)
                    foreach (Type t in types)
                        if (string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase)) matches.Add(t);
                if (matches.Count == 0)
                {
                    List<object> similar = new List<object>();
                    foreach (Type t in types)
                    {
                        if (Dyn.TypeName(t).IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) similar.Add(Dyn.TypeName(t));
                        if (similar.Count >= limit) break;
                    }
                    throw new ToolError("NOT_FOUND", "Type '" + typeName + "' not found in the loaded NX Open API.", "Similar types are listed in details.", similar);
                }
                if (matches.Count > 1)
                {
                    List<object> names = new List<object>();
                    foreach (Type t in matches) names.Add(Dyn.TypeName(t));
                    return R.Obj("ambiguous", true, "types", names);
                }
                Type type = matches[0];
                return R.Obj("type", Dyn.TypeName(type), "kind", type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class",
                    "base", type.BaseType == null ? null : Dyn.TypeName(type.BaseType), "members", Dyn.Members(type, query, limit));
            }

            List<object> typeHits = new List<object>();
            List<object> memberHits = new List<object>();
            foreach (Type t in types)
            {
                if (typeHits.Count < limit && t.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) typeHits.Add(Dyn.TypeName(t));
            }
            foreach (Type t in types)
            {
                if (memberHits.Count >= limit) break;
                try
                {
                    foreach (MemberInfo m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        if (m.MemberType != MemberTypes.Method && m.MemberType != MemberTypes.Property) continue;
                        MethodInfo mi = m as MethodInfo;
                        if (mi != null && mi.IsSpecialName) continue;
                        if (m.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        memberHits.Add(Dyn.TypeName(t) + " :: " + (mi != null ? Dyn.Signature(mi) : "property " + m.Name));
                        if (memberHits.Count >= limit) break;
                    }
                }
                catch (Exception) { }
            }
            return R.Obj("query", query, "types", typeHits, "members", memberHits,
                "hint", "Call again with type=<full type name> to list all members of one type.");
        }
    }
}
