// nx-ai-bridge kernel - late-bound access to the NX Open API.
//
// WHY: the receiver was written without access to an NX installation. A wrong member name in
// strongly typed code is a compile error that kills a whole handler file and says little.
// Going through Dyn turns it into a runtime ApiMismatch error that lists the members that
// really exist on that type in the running NX version - so one test round is enough to fix it,
// and the same handler source can adapt to NX 10 / 12 / newer.
//
// C# 5 only. This file must not reference NXOpen.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace NxAiBridge
{
    public class ApiMismatch : Exception
    {
        public string TypeName;
        public string Member;
        public List<string> Candidates;

        public ApiMismatch(string typeName, string member, string message, List<string> candidates) : base(message)
        {
            TypeName = typeName;
            Member = member;
            Candidates = candidates;
        }
    }

    public static class Dyn
    {
        private const BindingFlags Inst = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
        private const BindingFlags Stat = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

        // ------------------------------------------------------------ types and enums
        /// <summary>Find a type by full name in all loaded assemblies. Nested types: "NXOpen.View+Canned" or "NXOpen.View.Canned".</summary>
        public static Type FindType(string fullName)
        {
            Type t;
            if (TypeCache.TryGetValue(fullName, out t)) return t;
            List<string> names = new List<string>();
            names.Add(fullName);
            // try turning trailing dots into '+' (nested types)
            string n = fullName;
            for (int k = 0; k < 3; k++)
            {
                int dot = n.LastIndexOf('.');
                if (dot < 0) break;
                n = n.Substring(0, dot) + "+" + n.Substring(dot + 1);
                names.Add(n);
            }
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (string name in names)
                {
                    try { t = asm.GetType(name, false, false); } catch (Exception) { t = null; }
                    if (t != null) { TypeCache[fullName] = t; return t; }
                }
            }
            throw new ApiMismatch(fullName, null, "Type '" + fullName + "' does not exist in this NX version.", new List<string>());
        }

        public static object Enum(string typeName, string value)
        {
            Type t = FindType(typeName);
            try { return System.Enum.Parse(t, value, true); }
            catch (ArgumentException)
            {
                throw new ApiMismatch(typeName, value, "Enum " + typeName + " has no value '" + value + "'. Values: " + string.Join(", ", System.Enum.GetNames(t)),
                    new List<string>(System.Enum.GetNames(t)));
            }
        }

        public static Array MakeArray(string elementType, params object[] items)
        {
            Array arr = Array.CreateInstance(FindType(elementType), items.Length);
            for (int i = 0; i < items.Length; i++) arr.SetValue(items[i], i);
            return arr;
        }

        // ------------------------------------------------------------ properties
        public static bool Has(object target, string name)
        {
            if (target == null) return false;
            Type t = target.GetType();
            return t.GetProperty(name, Inst) != null || t.GetField(name, Inst) != null || t.GetMember(name, MemberTypes.Method, Inst).Length > 0;
        }

        public static object Get(object target, string name)
        {
            if (target == null) throw new NullReferenceException("Dyn.Get('" + name + "') on a null object");
            Type t = target.GetType();
            PropertyInfo p = FindProperty(t, name);
            if (p != null) return Invoke(delegate { return p.GetValue(target, null); });
            FieldInfo f = t.GetField(name, Inst);
            if (f != null) return f.GetValue(target);
            throw Mismatch(t, name, "property");
        }

        public static object GetPath(object target, string path)
        {
            foreach (string part in path.Split('.')) target = Get(target, part);
            return target;
        }

        public static void Set(object target, string name, object value)
        {
            if (target == null) throw new NullReferenceException("Dyn.Set('" + name + "') on a null object");
            Type t = target.GetType();
            PropertyInfo p = FindProperty(t, name);
            if (p != null && p.CanWrite)
            {
                object converted;
                int cost;
                if (!Coerce(value, p.PropertyType, false, out converted, out cost))
                    throw new ApiMismatch(TypeName(t), name, "Cannot assign " + DescribeValue(value) + " to " + TypeName(t) + "." + name + " (type " + TypeName(p.PropertyType) + ")" + EnumHint(p.PropertyType), Members(t, name, 30));
                Invoke(delegate { p.SetValue(target, converted, null); return null; });
                return;
            }
            FieldInfo f = t.GetField(name, Inst);
            if (f != null)
            {
                object converted;
                int cost;
                if (!Coerce(value, f.FieldType, false, out converted, out cost))
                    throw new ApiMismatch(TypeName(t), name, "Cannot assign " + DescribeValue(value) + " to field " + TypeName(t) + "." + name, Members(t, name, 30));
                f.SetValue(target, converted);
                return;
            }
            throw Mismatch(t, name, p != null ? "writable property" : "property");
        }

        /// <summary>Set a nested property: SetPath(builder, "Limits.EndExtend.Value.RightHandSide", "10").</summary>
        public static void SetPath(object target, string path, object value)
        {
            string[] parts = path.Split('.');
            for (int i = 0; i < parts.Length - 1; i++) target = Get(target, parts[i]);
            Set(target, parts[parts.Length - 1], value);
        }

        /// <summary>Like SetPath but returns false instead of throwing (for optional settings).</summary>
        public static bool TrySet(object target, string path, object value)
        {
            try { SetPath(target, path, value); return true; }
            catch (Exception) { return false; }
        }

        /// <summary>Set the first property path that exists; returns the path used.</summary>
        public static string SetAny(object target, string[] paths, object value)
        {
            Exception first = null;
            foreach (string path in paths)
            {
                try { SetPath(target, path, value); return path; }
                catch (ApiMismatch ex) { if (first == null) first = ex; }
            }
            throw first;
        }

        // ------------------------------------------------------------ methods
        /// <summary>
        /// Call a public instance method by name. Overloads are resolved by argument count and
        /// convertibility (strings convert to enums, numbers widen, arrays are re-typed).
        /// ref/out results are written back into <paramref name="args"/>.
        /// </summary>
        public static object Call(object target, string name, params object[] args)
        {
            if (target == null) throw new NullReferenceException("Dyn.Call('" + name + "') on a null object");
            return CallOn(target.GetType(), target, name, args, Inst);
        }

        public static object CallStatic(string typeName, string name, params object[] args)
        {
            return CallOn(FindType(typeName), null, name, args, Stat);
        }

        /// <summary>Like Call but returns null instead of throwing (for optional steps).</summary>
        public static object TryCall(object target, string name, params object[] args)
        {
            try { return Call(target, name, args); }
            catch (Exception) { return null; }
        }

        private static object CallOn(Type t, object target, string name, object[] args, BindingFlags flags)
        {
            if (args == null) args = new object[] { null }; // Call(x, "M", null) means one null argument
            MethodInfo best = null;
            object[] bestArgs = null;
            int bestCost = int.MaxValue;
            foreach (MemberInfo mi in t.GetMember(name, MemberTypes.Method, flags))
            {
                MethodInfo m = (MethodInfo)mi;
                if (m.IsGenericMethodDefinition) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != args.Length) continue;
                object[] converted = new object[args.Length];
                int total = 0;
                bool ok = true;
                for (int i = 0; i < ps.Length && ok; i++)
                {
                    int cost;
                    ok = Coerce(args[i], ps[i].ParameterType, ps[i].IsOut, out converted[i], out cost);
                    total += cost;
                }
                if (ok && total < bestCost) { best = m; bestArgs = converted; bestCost = total; }
            }
            if (best == null)
            {
                StringBuilder given = new StringBuilder();
                foreach (object a in args) { if (given.Length > 0) given.Append(", "); given.Append(DescribeValue(a)); }
                List<string> cands = Members(t, name, 30);
                throw new ApiMismatch(TypeName(t), name,
                    "No method " + TypeName(t) + "." + name + "(" + given + ") in this NX version. Similar members: " + string.Join(" | ", cands.ToArray()), cands);
            }
            object result = Invoke(delegate { return best.Invoke(target, bestArgs); });
            ParameterInfo[] bps = best.GetParameters();
            for (int i = 0; i < bps.Length; i++) if (bps[i].ParameterType.IsByRef) args[i] = bestArgs[i];
            return result;
        }

        private delegate object Thunk();

        private static object Invoke(Thunk thunk)
        {
            try { return thunk(); }
            catch (TargetInvocationException ex)
            {
                if (ex.InnerException != null) throw new DynInvocationException(ex.InnerException);
                throw;
            }
        }

        // ------------------------------------------------------------ conversion
        private static bool IsNumeric(Type t)
        {
            return t == typeof(double) || t == typeof(float) || t == typeof(int) || t == typeof(long) || t == typeof(short)
                || t == typeof(byte) || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(decimal);
        }

        private static bool Coerce(object arg, Type target, bool isOut, out object result, out int cost)
        {
            result = arg;
            cost = 0;
            if (target.IsByRef) target = target.GetElementType();
            if (arg == null)
            {
                if (!target.IsValueType) return true;
                if (isOut || Nullable.GetUnderlyingType(target) != null) { result = isOut ? Activator.CreateInstance(target) : null; return true; }
                return false;
            }
            Type at = arg.GetType();
            if (target.IsAssignableFrom(at)) { cost = (target == at) ? 0 : 1; return true; }
            if (target.IsEnum)
            {
                if (arg is string)
                {
                    try { result = System.Enum.Parse(target, (string)arg, true); cost = 2; return true; }
                    catch (ArgumentException) { return false; }
                }
                if (arg is int) { result = System.Enum.ToObject(target, (int)arg); cost = 3; return true; }
                return false;
            }
            if (IsNumeric(target) && IsNumeric(at))
            {
                double d = Convert.ToDouble(arg, CultureInfo.InvariantCulture);
                bool targetIsInt = target != typeof(double) && target != typeof(float) && target != typeof(decimal);
                if (targetIsInt && d != Math.Floor(d)) return false;
                try { result = Convert.ChangeType(arg, target, CultureInfo.InvariantCulture); }
                catch (Exception) { return false; }
                cost = 2;
                return true;
            }
            if (target.IsArray && arg is IEnumerable && !(arg is string))
            {
                Type et = target.GetElementType();
                ArrayList items = new ArrayList();
                foreach (object item in (IEnumerable)arg)
                {
                    object conv;
                    int c;
                    if (!Coerce(item, et, false, out conv, out c)) return false;
                    items.Add(conv);
                }
                Array arr = Array.CreateInstance(et, items.Count);
                for (int i = 0; i < items.Count; i++) arr.SetValue(items[i], i);
                result = arr;
                cost = 2;
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------ diagnostics
        private static PropertyInfo FindProperty(Type t, string name)
        {
            try { return t.GetProperty(name, Inst); }
            catch (AmbiguousMatchException)
            {
                // "new" properties hide inherited ones: take the most derived
                PropertyInfo found = null;
                foreach (PropertyInfo p in t.GetProperties(Inst))
                    if (p.Name == name && (found == null || p.DeclaringType.IsSubclassOf(found.DeclaringType))) found = p;
                return found;
            }
        }

        private static ApiMismatch Mismatch(Type t, string name, string kind)
        {
            List<string> cands = Members(t, name, 30);
            return new ApiMismatch(TypeName(t), name,
                TypeName(t) + " has no " + kind + " '" + name + "' in this NX version. Similar members: " + string.Join(" | ", cands.ToArray()), cands);
        }

        private static string EnumHint(Type t)
        {
            return t.IsEnum ? ". Enum values: " + string.Join(", ", System.Enum.GetNames(t)) : "";
        }

        private static string DescribeValue(object v)
        {
            if (v == null) return "null";
            if (v is string) return "\"" + v + "\"";
            return TypeName(v.GetType());
        }

        public static string TypeName(Type t)
        {
            if (t == null) return "?";
            if (t.IsByRef) return "ref " + TypeName(t.GetElementType());
            if (t.IsArray) return TypeName(t.GetElementType()) + "[]";
            if (t == typeof(void)) return "void";
            if (t == typeof(string)) return "string";
            if (t == typeof(double)) return "double";
            if (t == typeof(int)) return "int";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(object)) return "object";
            return (t.FullName ?? t.Name).Replace('+', '.');
        }

        public static string Signature(MethodInfo m)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(TypeName(m.ReturnType)).Append(' ').Append(m.Name).Append('(');
            ParameterInfo[] ps = m.GetParameters();
            for (int i = 0; i < ps.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                if (ps[i].IsOut) sb.Append("out ").Append(TypeName(ps[i].ParameterType.GetElementType()));
                else sb.Append(TypeName(ps[i].ParameterType));
                sb.Append(' ').Append(ps[i].Name);
            }
            return sb.Append(')').ToString();
        }

        private static bool Similar(string member, string wanted)
        {
            if (wanted == null) return true;
            string a = member.ToLowerInvariant();
            string b = wanted.ToLowerInvariant();
            if (a == b || a.Contains(b) || (a.Length >= 4 && b.Contains(a))) return true;
            int common = 0;
            while (common < a.Length && common < b.Length && a[common] == b[common]) common++;
            return common >= 5;
        }

        /// <summary>Human readable public members of a type; filter = null lists everything.</summary>
        public static List<string> Members(Type t, string filter, int limit)
        {
            List<string> exact = new List<string>();
            List<string> others = new List<string>();
            if (t.IsEnum)
            {
                foreach (string v in System.Enum.GetNames(t)) if (Similar(v, filter)) others.Add("enum value " + v);
            }
            foreach (PropertyInfo p in t.GetProperties(Inst | BindingFlags.Static))
            {
                if (!Similar(p.Name, filter)) continue;
                string line = "property " + TypeName(p.PropertyType) + " " + p.Name + " {" + (p.CanRead ? " get;" : "") + (p.CanWrite ? " set;" : "") + " }";
                if (filter != null && string.Equals(p.Name, filter, StringComparison.OrdinalIgnoreCase)) exact.Add(line); else others.Add(line);
            }
            foreach (MethodInfo m in t.GetMethods(Inst | BindingFlags.Static))
            {
                if (m.IsSpecialName || m.DeclaringType == typeof(object) || m.DeclaringType == typeof(MarshalByRefObject)) continue;
                if (!Similar(m.Name, filter)) continue;
                string line = "method " + Signature(m);
                if (filter != null && string.Equals(m.Name, filter, StringComparison.OrdinalIgnoreCase)) exact.Add(line); else others.Add(line);
            }
            others.Sort(StringComparer.Ordinal);
            exact.AddRange(others);
            if (exact.Count > limit) { int more = exact.Count - limit; exact.RemoveRange(limit, more); exact.Add("... " + more + " more"); }
            return exact;
        }
    }

    /// <summary>Carries the real exception thrown by an NX Open method that was called through Dyn.</summary>
    public class DynInvocationException : Exception
    {
        public DynInvocationException(Exception inner) : base(inner.Message, inner) { }
    }
}
