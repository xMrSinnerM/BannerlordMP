// Usage: bldump <TypeName|Full.Type.Name> [memberFilter]   or   bldump "~substring" to list matching type names.
// Reads the Bannerlord reference assemblies from the NuGet cache (restore src/BannerlordMP first).
// BLDUMP_EXTRA=path1.dll:path2.dll adds more assemblies.
using System.Reflection;
static string N(Type t){ try { if(t.IsGenericType){ var n=t.Name.Split('`')[0]; return n+"<"+string.Join(",",t.GetGenericArguments().Select(N))+">";} return t.Name;} catch { return t.Name; } }
var dirs = new[]{
 Directory.GetDirectories(Environment.GetEnvironmentVariable("HOME")+"/.nuget/packages/bannerlord.referenceassemblies.core")[0]+"/ref/net472",
 Directory.GetDirectories(Environment.GetEnvironmentVariable("HOME")+"/.nuget/packages/bannerlord.referenceassemblies.sandbox")[0]+"/ref/net472",
 Directory.GetDirectories(Environment.GetEnvironmentVariable("HOME")+"/.nuget/packages/bannerlord.referenceassemblies.native")[0]+"/ref/net472",
 Directory.GetDirectories(Environment.GetEnvironmentVariable("HOME")+"/.nuget/packages/microsoft.netframework.referenceassemblies.net472")[0]+"/build/.NETFramework/v4.7.2",
};
var files = dirs.SelectMany(d => Directory.GetFiles(d, "*.dll")).ToList();
files.AddRange(Directory.GetFiles(dirs[3]+"/Facades","*.dll"));
var extra = Environment.GetEnvironmentVariable("BLDUMP_EXTRA"); if (!string.IsNullOrEmpty(extra)) files.AddRange(extra.Split(':'));
using var mlc = new MetadataLoadContext(new PathAssemblyResolver(files), "mscorlib");
var pattern = args[0];
var memberFilter = args.Length > 1 ? args[1] : null;
foreach (var f in files.Where(f=>Path.GetFileName(f).StartsWith("TaleWorlds")||Path.GetFileName(f).StartsWith("SandBox")||Path.GetFileName(f).StartsWith("Steamworks"))) {
  Assembly a; try { a = mlc.LoadFromAssemblyPath(f);} catch { continue; }
  Type[] ts; try { ts = a.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types.Where(t=>t!=null).ToArray(); }
  foreach (var t in ts) {
    if (pattern.StartsWith("~")) { if ((t.FullName??"").IndexOf(pattern.Substring(1), StringComparison.OrdinalIgnoreCase) >= 0) Console.WriteLine(t.FullName); continue; } // LISTMODE
    if (t.FullName != pattern && t.Name != pattern) continue;
    Console.WriteLine($"== {t.FullName} : {t.BaseType?.Name} [{Path.GetFileName(f)}]");
    var bf = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly;
    foreach (var m in t.GetMembers(bf)) {
      if (memberFilter!=null && !m.Name.Contains(memberFilter, StringComparison.OrdinalIgnoreCase)) continue;
      try {
      string s = m switch {
        MethodInfo mi => $"{(mi.IsPublic?"public":"nonpub")} {(mi.IsStatic?"static ":"")}{(mi.IsVirtual?"virtual ":"")}{N(mi.ReturnType)} {mi.Name}({string.Join(", ", mi.GetParameters().Select(p=>N(p.ParameterType)+" "+p.Name))})",
        PropertyInfo pi => $"prop {N(pi.PropertyType)} {pi.Name} {(pi.GetMethod?.IsPublic==true?"get":"")} {(pi.SetMethod?.IsPublic==true?"set":(pi.SetMethod!=null?"privset":""))}",
        FieldInfo fi => $"field {(fi.IsPublic?"public":"nonpub")} {(fi.IsStatic?"static ":"")}{N(fi.FieldType)} {fi.Name}",
        ConstructorInfo ci => $"ctor({string.Join(", ", ci.GetParameters().Select(p=>N(p.ParameterType)+" "+p.Name))})",
        EventInfo ei => $"event {ei.EventHandlerType?.Name} {ei.Name}",
        _ => $"{m.MemberType} {m.Name}" };
      Console.WriteLine("  "+s);
      } catch (Exception e) { Console.WriteLine("  ?? "+m.Name+" "+e.GetType().Name); }
    }
  }
}
