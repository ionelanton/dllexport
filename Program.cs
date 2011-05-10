﻿//-----------------------------------------------------------------------
// <copyright company="CoApp Project">
//     Original Copyright (c) 2009 Microsoft Corporation. All rights reserved.
//     Changes Copyright (c) 2010  Garrett Serack. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

// -----------------------------------------------------------------------
// Original Code: 
// (c) 2009 Microsoft Corporation -- All rights reserved
// This code is licensed under the MS-PL
// http://www.opensource.org/licenses/ms-pl.html
// Courtesy of the Open Source Techology Center: http://port25.technet.com
// -----------------------------------------------------------------------

namespace CoApp.DllExport {
    using System;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Reflection.Emit;
    using Toolkit.Exceptions;
    using Toolkit.Extensions;
    using Toolkit.Utility;

    public class DllExportUtility {
        private static string help =
            @"
DllExport for .NET 4.0
----------------------

DllExport will create a DLL that exposes standard C style function calls
which are transparently thunked to static methods in a .NET assembly.

To export a static method in a .NET class, mark each method with an 
attribute called DllExportAttribute (see help for an example attribute class)

Usage:
   DllExport [options] Assembly.dll -- This creates the native thunks in an 
                                       assembly called $TargetAssembly.dll

                                       This is good for development--it 
                                       preserves the original assembly and 
                                       allows for easy debugging.
   Options:
        --merge                     -- this will generate the thunking 
                                       functions and merge them into the 
                                       target assembly. 

                                       This is good for when you want to 
                                       produce a release build.

                                       ** Warning **
                                       This overwrites the target assembly.

        --keep-temp-files           -- leaves the working files in the 
                                       current directory. 

        --rescan-tools              -- causes the tool to search for its
                                       dependent tools (ilasm and ildasm)
                                       instead of using the cached values.

        --nologo                    -- suppresses informational messages.

        --output-filename=<file>    -- creates the native dll as <file>

        --create-lib                -- creates a lib file

        --platform=<arch>           -- outputs the native DLL as arch 
                                       (x64 or x86)

        --create-header=<file>      -- creates a C header file for the library

   More Help:

        DllExport --help            -- Displays this help.
 
        DllExport --sampleClass     -- Displays the DllExportAttribute 
                                       source code that you should include
                                       in your assembly.

        DllExport --sampleUsage     -- Displays some examples of using the 
                                       DllExport attribute.";

        // internal static List<ExportableMember> members = new List<ExportableMember>();
        private static bool keepTempFiles;
        private static bool quiet;
        private static bool debug;
        private static string finalOuputFilename;
        private static string headerFilename;
        private static bool createLib;
        private static string platform = "x86";
        private static List<string> exports = new List<string> {"EXPORTS"};

        private static List<string> functions = new List<string> {
            @"#pragma once
#ifdef __cplusplus
#define __EXTERN_C extern ""C"" 
#else
#define __EXTERN_C 
#endif 
"
        };

        private static Dictionary<CallingConvention, Type> ModOpt = new Dictionary<CallingConvention, Type> {
            {CallingConvention.Cdecl, typeof (System.Runtime.CompilerServices.CallConvCdecl)},
            {CallingConvention.FastCall, typeof (System.Runtime.CompilerServices.CallConvFastcall)},
            {CallingConvention.Winapi, typeof (System.Runtime.CompilerServices.CallConvStdcall)},
            {CallingConvention.StdCall, typeof (System.Runtime.CompilerServices.CallConvStdcall)},
            {CallingConvention.ThisCall, typeof (System.Runtime.CompilerServices.CallConvThiscall)}
        };

        private static Dictionary<CallingConvention, string> CCallingConvention = new Dictionary<CallingConvention, string> {
            {CallingConvention.Cdecl, "__cdecl"},
            {CallingConvention.FastCall, "__fastcall"},
            {CallingConvention.Winapi, "__stdcall"},
            {CallingConvention.StdCall, "__stdcall"},
            {CallingConvention.ThisCall, "__thiscall"}
        };
        private Dictionary<string, string> enumTypeDefs = new Dictionary<string, string>();

        internal class ExportableMember {
            internal MemberInfo member;
            internal string exportedName;
            internal CallingConvention callingConvention;
        }

        static void Delete(string filename) {
            if(keepTempFiles) {
                if(!quiet)
                    Console.WriteLine("   Warning: leaving temporary file [{0}]", filename);
            }
            else
                File.Delete(filename);
        }

        static void Main(string[] args) {
            new DllExportUtility().main(args);
        }

        private string CType( Type t, UnmanagedType? customType = null ) {
            
            switch(t.Name) {
                case "Byte":
                case "byte":
                    return "unsigned __int8";
                case "SByte":
                case "sbyte":
                    return "__int8";
                case "int":
                case "Int32":
                    return "__int32";
                case "uint":
                case "UInt32":
                    return "unsigned __int32";
                case "Int16":
                case "short":
                    return "__int16";
                case "UInt16":
                case "ushort":
                    return "unsigned __int16";
                case "Int64":
                case "long":
                    return "__int64";
                case "UInt64":
                case "ulong": 
                    return "unsigned __int64";
                case "Single":
                case "float":
                    return "float";
                case "Double":
                case "double":
                    return "double";
                case "Char":
                case "char":
                    return "wchar_t";
                case "bool":
                case "Boolean":
                    return "bool";
                case "string":
                case "String":
                    if (customType.HasValue && customType.Value == UnmanagedType.LPWStr ) {
                        return "const wchar_t*";    
                    }
                    return "const char_t*";


                case "IntPtr":
                    return "void*";
            }

            if( t.IsEnum ) {
                if( enumTypeDefs.ContainsKey(t.Name)) {
                    return t.Name;
                }
                var enumType = CType(t.GetEnumUnderlyingType()); 
                
                
                var enumValues = t.GetEnumValues();
                
                var first = true;
                var evitems = string.Empty;
                foreach( var ev in enumValues) {
                    evitems+= "{2}\r\n    {0} = {1}".format(t.GetEnumName(ev), (int)ev, first? "":",");
                    first = false;
                }

                var tyepdefenum = "typedef enum  {{ {2}\r\n}} {0};\r\n".format(t.Name, enumType, evitems);/* : {1} */
                enumTypeDefs.Add(t.Name, tyepdefenum);
                return t.Name;
            }

            return "/* UNKNOWN : {0} */".format(t.Name);
        }

        private string GenerateShimAssembly( string originalAssemblyPath ) {
            Assembly assembly;

            try {
                assembly = Assembly.Load(File.ReadAllBytes(originalAssemblyPath));
            }
            catch {
                throw new ConsoleException("Error: unable to load the specified original assembly \r\n   [{0}].\r\n\r\nMost likely, it has already been modified--and can't be modified again.", originalAssemblyPath);
            }
           
            var members = GetExportableMembers(assembly);

            if (members.Count() == 0) {
                throw new ConsoleException("No members found with DllExport attributes in the target assembly \r\n   [{0}]", originalAssemblyPath);
            }

            var assemblyName = new AssemblyName("$" + assembly.GetName());
            var assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Save);
            var moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name, assemblyName.Name + ".dll");

            var index = 0;
            var modopts = new Type[1];

            foreach (ExportableMember exportableMember in members) {
                var methodInfo = exportableMember.member as MethodInfo;
                if (methodInfo != null) {
                    ParameterInfo[] pinfo = methodInfo.GetParameters();
                    var parameterTypes = new Type[pinfo.Length];
                    var requiredCustomModifiers = new Type[pinfo.Length][];
                    var optionalCustomModifiers = new Type[pinfo.Length][];
                    var customAttributes = new object[pinfo.Length][];
                    for (int i = 0; i < pinfo.Length; i++) {
                        parameterTypes[i] = pinfo[i].ParameterType;
                        requiredCustomModifiers[i] =  pinfo[i].GetRequiredCustomModifiers();
                        optionalCustomModifiers[i] = pinfo[i].GetOptionalCustomModifiers();
                        customAttributes[i] = pinfo[i].GetCustomAttributes(false);
                    }

                    modopts[0] = ModOpt[exportableMember.callingConvention];

                    var decl = @" __EXTERN_C __declspec( dllimport ) {1} {0} {2}(".format(CCallingConvention[exportableMember.callingConvention], CType(methodInfo.ReturnType), exportableMember.exportedName);
                    var pc = 0;
                    foreach (var parameterInfo in pinfo) {
                        var customAttributeData = parameterInfo.GetCustomAttributesData();
                        UnmanagedType? customType = null;
                        foreach (var ctorarg in
                            customAttributeData.Where(cattr => cattr.Constructor.DeclaringType.Name == "MarshalAsAttribute").SelectMany(cattr => cattr.ConstructorArguments.Where(ctorarg => ctorarg.ArgumentType.Name == "UnmanagedType"))) {
                            customType = (UnmanagedType) ctorarg.Value;
                        }
                        decl += "{2}{0} {1}".format(CType(parameterInfo.ParameterType, customType), parameterInfo.Name, (pc == 0) ? "" : ", ");
                        pc++;
                    }
                    decl += ");\r\n";
                    functions.Add(decl);

                    // stdcall functions need to have the @size after the name.
                    if (platform == "x86" && exportableMember.callingConvention == CallingConvention.StdCall) {
                        exportableMember.exportedName = exportableMember.exportedName + "@" + (pc*4);
                    }
                    exports.Add(exportableMember.exportedName);
                    
                    MethodBuilder methodBuilder = moduleBuilder.DefineGlobalMethod(methodInfo.Name, MethodAttributes.Static | MethodAttributes.Public, CallingConventions.Standard, methodInfo.ReturnType, null, modopts, parameterTypes,requiredCustomModifiers, optionalCustomModifiers);
                    ILGenerator ilGenerator = methodBuilder.GetILGenerator();

                    // this is to pull the ol' swicheroo later.
                    ilGenerator.Emit(OpCodes.Ldstr, string.Format(".export [{0}] as {1}", index++, exportableMember.exportedName));
                    

                    
                    int n = 0;
                    foreach (ParameterInfo parameterInfo in pinfo) {
                        switch (n) {
                            case 0:
                                ilGenerator.Emit(OpCodes.Ldarg_0);
                                break;
                            case 1:
                                ilGenerator.Emit(OpCodes.Ldarg_1);
                                break;
                            case 2:
                                ilGenerator.Emit(OpCodes.Ldarg_2);
                                break;
                            case 3:
                                ilGenerator.Emit(OpCodes.Ldarg_3);
                                break;
                            default:
                                ilGenerator.Emit(OpCodes.Ldarg_S, (byte)n);
                                break;
                        }
                        n++;

                        var pbuilder = methodBuilder.DefineParameter(n, parameterInfo.Attributes, parameterInfo.Name); //1-based... *sigh*
                        
                        // Copy over custom attributes (important for marshalling)
                        var customAttributeData = parameterInfo.GetCustomAttributesData();
                        foreach (var attr in customAttributeData) {
                            object[] cargs = new object[attr.ConstructorArguments.Count];
                            for (int j = 0; j < attr.ConstructorArguments.Count; j++) {
                                cargs[j] = attr.ConstructorArguments[j].Value;
                            }
                            var pi = new List<PropertyInfo>();
                            var fi = new List<FieldInfo>();
                            var pid = new List<object>();
                            var fid = new List<object>();
                            foreach (var ni in attr.NamedArguments) {
                                if (ni.MemberInfo is PropertyInfo) {
                                    pi.Add(ni.MemberInfo as PropertyInfo);
                                    pid.Add(ni.TypedValue.Value);
                                }
                                if (ni.MemberInfo is FieldInfo) {
                                    fi.Add(ni.MemberInfo as FieldInfo);
                                    fid.Add(ni.TypedValue.Value);
                                }
                            }

                            var cb = new CustomAttributeBuilder(attr.Constructor, cargs, pi.ToArray(), pid.ToArray(), fi.ToArray(), fid.ToArray());
                            pbuilder.SetCustomAttribute(cb);
                            
                        }
                    }
                    ilGenerator.EmitCall(OpCodes.Call, methodInfo, null);
                    ilGenerator.Emit(OpCodes.Ret);
                }
            }

            moduleBuilder.CreateGlobalFunctions();

            var outputFilename = assemblyName.Name + ".dll";

            if( File.Exists(outputFilename)) {
                File.Delete(outputFilename);
            }
            assemblyBuilder.Save(outputFilename);

            return outputFilename;
        }

        private IEnumerable<ExportableMember> GetExportableMembers(Assembly assembly) {
            // var memberInfos = assembly.GetTypes().Aggregate(Enumerable.Empty<MemberInfo>(), (current, type) => current.Union(type.FindMembers(MemberTypes.Method, BindingFlags.Public | BindingFlags.Static, (memberInfo, obj) => memberInfo.GetCustomAttributes(false).Any(attrib => attrib.GetType().Name.Equals("DllExportAttribute")), null)));

            foreach (var mi in assembly.GetTypes().Aggregate(Enumerable.Empty<MemberInfo>(), (current, type) => current.Union(type.GetMethods(BindingFlags.Public | BindingFlags.Static)))) {
                foreach (var attrib in from attrib in mi.GetCustomAttributes(false) where attrib.GetType().Name.Equals("DllExportAttribute") select attrib) {
                    ExportableMember member = null;
                    try {
                        member = new ExportableMember {
                            member = mi,
                            exportedName = attrib.GetType().GetProperty("ExportedName").GetValue(attrib, null).ToString(),
                            callingConvention = (CallingConvention) attrib.GetType().GetProperty("CallingConvention").GetValue(attrib, null)
                        };
                    }
                    catch (Exception) {
                        Console.Error.WriteLine( "Warning: Found DllExport on Member {0}, but unable to get ExportedName or CallingConvention property.");
                    }

                    if (member != null)
                        yield return member;
                    
                }
            }
        }


        int main(string[] args) {
            // int firstArg = 0;
            bool mergeAssemblies = false;

            var options = args.Switches();
            var parameters = args.Parameters();


             foreach (string arg in options.Keys) {
                 IEnumerable<string> argumentParameters = options[arg];

                 switch (arg) {
                     case "nologo":
                         this.Assembly().SetLogo("");
                         quiet = true;
                         break;

                     case "merge":
                         mergeAssemblies = true;
                         break;

                     case "keep-temp-files":
                         keepTempFiles = true;
                         break;

                     case "rescan-tools":
                         ProgramFinder.IgnoreCache = true;
                         break;

                     case "debug":
                         debug = true;
                         break;

                     case "sampleusage":
                         SampleUsage();
                         return 0;

                     case "sampleclass":
                         SampleClass();
                         return 0;

                     case "output-filename":
                         finalOuputFilename = argumentParameters.FirstOrDefault();
                         break;

                     case "create-header":
                         headerFilename = argumentParameters.FirstOrDefault();
                         break;

                     case "platform":
                         platform = (argumentParameters.FirstOrDefault() ?? "x86" ).Equals("x64",StringComparison.CurrentCultureIgnoreCase) ? "x64" : "x86";
                         break;
                        
                     case "create-lib":
                         createLib = true;
                         break;

                     case "help":
                         Help();
                         return 0;

                     default:
                         Logo();
                         return Fail("Error: unrecognized switch:{0}", arg );
                 }
             }


             if (parameters.Count() != 1  ) {
                Help();
                return 0;
            }

            if(!quiet)
                Logo();

            var ILDasm = new ProcessUtility(ProgramFinder.ProgramFilesAndDotNet.ScanForFile("ildasm.exe", "4.0.30319.1"));
            var ILAsm = new ProcessUtility(ProgramFinder.ProgramFilesAndDotNet.ScanForFile("ilasm.exe", "4.0.30319.1"));
            var Lib = new ProcessUtility(ProgramFinder.ProgramFilesAndDotNet.ScanForFile("lib.exe"));


            var originalAssemblyPath = parameters.First().GetFullPath();

            if(!File.Exists(originalAssemblyPath)) {
                return Fail("Error: the specified original assembly \r\n   [{0}]\r\ndoes not exist.", originalAssemblyPath);
            }

            var shimAssemblyPath = GenerateShimAssembly(originalAssemblyPath);
            finalOuputFilename = string.IsNullOrEmpty(finalOuputFilename) ? (mergeAssemblies ? originalAssemblyPath : shimAssemblyPath) : finalOuputFilename;

            var temporaryIlFilename = shimAssemblyPath + ".il";
            var rc = ILDasm.Exec(@"/text /nobar /typelist ""{0}""", shimAssemblyPath);
            if(0 != rc) {
                return Fail("Error: unable to disassemble the temporary assembly\r\n   [{0}]\r\nMore Information:\r\n{1}", shimAssemblyPath, ILDasm.StandardOut);
            }
            Delete(shimAssemblyPath); // eliminate it regardless of result.
            var ilSource = System.Text.RegularExpressions.Regex.Replace(ILDasm.StandardOut, @"IL_0000:.*ldstr.*\""(?<x>.*)\""", "${x}");

            if(mergeAssemblies) {
                var start = ilSource.IndexOf("\r\n.method");
                var end = ilSource.LastIndexOf("// end of global method");
                ilSource = ilSource.Substring(start, end - start);

                // arg! needed this to make sure the resources came out. grrr
                rc = ILDasm.Exec(@"/nobar /typelist ""{0}"" /out=""{1}""", originalAssemblyPath, temporaryIlFilename);
                rc = ILDasm.Exec(@"/nobar /text /typelist ""{0}""", originalAssemblyPath);
                if(0 != rc) {
                    return Fail("Error: unable to disassemble the target assembly\r\n   [{0}]\r\nMore Information:\r\n{1}", shimAssemblyPath, ILDasm.StandardOut);
                }
                var ilTargetSource = ILDasm.StandardOut;

                start = Math.Min(ilTargetSource.IndexOf(".method"), ilTargetSource.IndexOf(".class"));
                ilSource = ilTargetSource.Substring(0, start) + ilSource + ilTargetSource.Substring(start);
            }

            File.WriteAllText(temporaryIlFilename, ilSource);
            rc = ILAsm.Exec(@"{3} /dll {2} /output={0} ""{1}""", shimAssemblyPath, temporaryIlFilename, debug ? "/debug" : "", platform == "x64" ? "/X64" : "");

            if (!debug)
                Delete(temporaryIlFilename); // delete temp file regardless of result.

            if (0 != rc) {
                return Fail("Error: unable to assemble the merged assembly\r\n   [{0}]\r\n   [{1}]\r\nMore Information:\r\n{2}", shimAssemblyPath, temporaryIlFilename, ILAsm.StandardError);
            }

            if (originalAssemblyPath.Equals(finalOuputFilename, StringComparison.CurrentCultureIgnoreCase)) {
                File.Delete(originalAssemblyPath + ".orig");
                File.Move(originalAssemblyPath, originalAssemblyPath + ".orig");
            }
            File.Delete(finalOuputFilename);
            File.Move(shimAssemblyPath, finalOuputFilename);

            if (!quiet) {
                Console.WriteLine("Created Exported functions in Assembly: {0}", finalOuputFilename);
            }

            if( createLib ) {
                var defFile = Path.GetFileNameWithoutExtension(finalOuputFilename) + ".def";
                var libFile = Path.GetFileNameWithoutExtension(finalOuputFilename) + ".lib";
                
                File.WriteAllLines(defFile,exports);
                Lib.ExecNoRedirections("/NOLOGO /machine:{0} /def:{1} /OUT:{2}", platform, defFile, libFile);

            }

            if (headerFilename != null ) {

                foreach(var e in enumTypeDefs) {
                    functions.Insert(1, e.Value);
                }
                File.WriteAllLines(headerFilename, functions);
            }

            return 0;
        }



        public static void SampleClass() {
            using(new ConsoleColors(ConsoleColor.Green, ConsoleColor.Black))
                Console.WriteLine(@"
using System;
using System.Runtime.InteropServices;

/// <summary>
/// This class is used by the DllExport utility to generate a C-style
/// native binding for any static methods in a .NET assembly.
/// 
/// Namespace is not important--feel free to set the namespace to anything
/// convenient for your project.
/// -----------------------------------------------------------------------
/// (c) 2009 Microsoft Corporation -- All rights reserved
/// This code is licensed under the MS-PL
/// http://www.opensource.org/licenses/ms-pl.html
/// Courtesy of the Open Source Techology Center: http://port25.technet.com
/// -----------------------------------------------------------------------
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class DllExportAttribute: Attribute {
    public DllExportAttribute(string exportName) 
        : this(CallingConvention.StdCall,exportName) {
    }

    public DllExportAttribute(CallingConvention convention, string name) {
        ExportedName = name;
        this.CallingConvention = convention;
    }

    public string ExportedName { get; set; }
    public CallingConvention CallingConvention { get; set; }
}");

        }

        public static void SampleUsage() {
            using(new ConsoleColors(ConsoleColor.Green, ConsoleColor.Black))
                Console.WriteLine(@"
DllExport Usage
----------------

To use the DllExport Attribute in your code, include the class in your 
project. Namespace is not important.

On any method you wish to export as a C-style function, simply use the 
attribute on any static method in a class:

...

// example 1
// note the exported function name doesn't have to match the method name
[DllExport(""myFunction"")]     
public static int MyFunction( int age, string name ){
   // ....
}

// example 2
// On this example, we've marked set the calling convention to CDECL
[DllExport( CallingConvention.Cdecl, ""myNextFunction"")]     
public static int MyFunctionTwo( float someParameter, string name ){
   // ....
}

");
        }

        #region fail/help/logo
        public static int Fail(string text, params object[] par) {
            using(new ConsoleColors(ConsoleColor.Red, ConsoleColor.Black))
                Console.WriteLine("Error:{0}", text.format(par));
            return 1;
        }

        static int Help() {
            using(new ConsoleColors(ConsoleColor.White, ConsoleColor.Black))
                help.Print();
            return 0;
        }

        void Logo() {
            using(new ConsoleColors(ConsoleColor.Cyan, ConsoleColor.Black))
                this.Assembly().Logo().Print();
            this.Assembly().SetLogo("");
        }
        #endregion
    }
}
