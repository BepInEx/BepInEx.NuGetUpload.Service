using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace NuGetUpload.Utils
{
    public static class AssemblyStripper
    {
        public static void StripAssembly(string path, ISet<string> allowedNames = null)
        {
            using var module = ModuleDefMD.Load(File.ReadAllBytes(path));
            if (allowedNames != null && !allowedNames.Contains(module.Assembly.Name))
                throw new ArgumentException($"Assembly {module.Assembly.Name} is not allowed list");
            allowedNames?.Remove(module.Assembly.Name);
            foreach (var typeDef in module.GetTypes())
            {
                Strip(typeDef);
                Publicize(typeDef);
            }
            module.Write(path);
        }

        private static void Publicize(TypeDef td)
        {
            static bool HasCompilerGenerated(IHasCustomAttribute ca) =>
                ca.CustomAttributes.Any(c =>
                    c.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");

            if (HasCompilerGenerated(td))
                return;

            td.Attributes &= ~TypeAttributes.VisibilityMask;
            if (td.IsNested)
                td.Attributes |= TypeAttributes.NestedPublic;
            else
                td.Attributes |= TypeAttributes.Public;
            
            foreach (var methodDef in td.Methods)
            {
                if (HasCompilerGenerated(methodDef))
                    continue;
                methodDef.Attributes &= ~MethodAttributes.MemberAccessMask;
                methodDef.Attributes |= MethodAttributes.Public;
            }
            
            foreach (var fieldDef in td.Fields)
            {
                if (HasCompilerGenerated(fieldDef))
                    continue;

                fieldDef.Attributes &= ~FieldAttributes.FieldAccessMask;
                fieldDef.Attributes |= FieldAttributes.Public;
            }
        }

        private static void Strip(TypeDef td)
        {
            if (td.IsEnum || td.IsInterface)
                return;
            
            foreach (var methodDef in td.Methods)
            {
                if (!methodDef.HasBody)
                    continue;
                var newBody = new CilBody();
                newBody.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                newBody.Instructions.Add(Instruction.Create(OpCodes.Throw));
                methodDef.Body = newBody;
                methodDef.IsAggressiveInlining = false;
                methodDef.IsNoInlining = true;
            }
        }
    }
}
