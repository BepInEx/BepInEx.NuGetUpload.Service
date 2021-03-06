using System;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using FieldAttributes = dnlib.DotNet.FieldAttributes;
using MethodAttributes = dnlib.DotNet.MethodAttributes;
using MethodImplAttributes = dnlib.DotNet.MethodImplAttributes;
using TypeAttributes = dnlib.DotNet.TypeAttributes;

namespace NuGetUpload.Utils;

public static class AssemblyStripper
{
    private static readonly ConstructorInfo AttributeCtor = typeof(Attribute).GetConstructor(
        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

    public static void StripAssembly(ModuleDefMD module, bool publicize = true)
    {
        var (pubType, pubCtor) = publicize ? CreatePublicizedAttribute(module) : (null, null);

        foreach (var typeDef in module.GetTypes())
        {
            if (typeDef == pubType)
                continue;

            Strip(typeDef);
            if (publicize)
                Publicize(typeDef, pubCtor);
        }
    }

    private static (TypeDef, MethodDef) CreatePublicizedAttribute(ModuleDef module)
    {
        TypeDef td = new TypeDefUser("System.Runtime.CompilerServices", "PublicizedAttribute",
            module.Import(typeof(Attribute)));
        td.Attributes |= TypeAttributes.Public | TypeAttributes.Sealed;
        MethodDef ctor = new MethodDefUser(".ctor",
            MethodSig.CreateInstance(module.CorLibTypes.Void, module.CorLibTypes.Int32),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName |
            MethodAttributes.Public);
        td.Methods.Add(ctor);
        ctor.Body = new CilBody();
        ctor.Body.Instructions.Add(OpCodes.Ldarg_0.ToInstruction());
        ctor.Body.Instructions.Add(OpCodes.Call.ToInstruction(module.Import(AttributeCtor)));
        ctor.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
        module.Types.Add(td);
        return (td, ctor);
    }

    private static int GetAccessType(FieldDef fd)
    {
        return fd.Access switch
        {
            FieldAttributes.Public       => 0,
            FieldAttributes.Private      => 1,
            FieldAttributes.PrivateScope => 1,
            FieldAttributes.Family       => 2,
            FieldAttributes.Assembly     => 3,
            FieldAttributes.FamORAssem   => 4,
            FieldAttributes.FamANDAssem  => 5,
            _                            => throw new NotImplementedException($"Unrecognized access type: {fd}")
        };
    }

    private static int GetAccessType(MethodDef md)
    {
        return md.Access switch
        {
            MethodAttributes.Public       => 0,
            MethodAttributes.Private      => 1,
            MethodAttributes.PrivateScope => 1,
            MethodAttributes.Family       => 2,
            MethodAttributes.Assembly     => 3,
            MethodAttributes.FamORAssem   => 4,
            MethodAttributes.FamANDAssem  => 5,
            _                             => throw new NotImplementedException($"Unrecognized access type: {md}")
        };
    }

    private static int GetAccessType(TypeDef td)
    {
        return (td.Attributes & TypeAttributes.VisibilityMask) switch
        {
            TypeAttributes.Public            => 0,
            TypeAttributes.NotPublic         => 1,
            TypeAttributes.NestedPublic      => 0,
            TypeAttributes.NestedPrivate     => 1,
            TypeAttributes.NestedFamily      => 2,
            TypeAttributes.NestedAssembly    => 3,
            TypeAttributes.NestedFamORAssem  => 4,
            TypeAttributes.NestedFamANDAssem => 5,
            _                                => throw new NotImplementedException($"Unrecognized access type: {td}")
        };
    }

    private static void Publicize(TypeDef td, ICustomAttributeType pubAttribute)
    {
        if (!td.IsNested && !td.IsPublic || td.IsNested && !td.IsNestedPublic)
            td.CustomAttributes.Add(new CustomAttribute(pubAttribute,
                new[] { new CAArgument(td.Module.CorLibTypes.Int32, GetAccessType(td)) }));

        td.Attributes &= ~TypeAttributes.VisibilityMask;
        if (td.IsNested)
            td.Attributes |= TypeAttributes.NestedPublic;
        else
            td.Attributes |= TypeAttributes.Public;

        foreach (var methodDef in td.Methods)
        {
            if (methodDef.IsCompilerControlled)
                continue;

            if (!methodDef.IsPublic)
                methodDef.CustomAttributes.Add(new CustomAttribute(pubAttribute,
                    new[] { new CAArgument(td.Module.CorLibTypes.Int32, GetAccessType(methodDef)) }));

            methodDef.Attributes &= ~MethodAttributes.MemberAccessMask;
            methodDef.Attributes |= MethodAttributes.Public;
        }

        var eventNames = td.Events.Select(e => e.Name).ToHashSet();
        foreach (var fieldDef in td.Fields)
        {
            if (fieldDef.IsCompilerControlled)
                continue;

            // Skip event backing fields
            if (eventNames.Contains(fieldDef.Name))
                continue;

            if (!fieldDef.IsPublic)
                fieldDef.CustomAttributes.Add(new CustomAttribute(pubAttribute,
                    new[] { new CAArgument(td.Module.CorLibTypes.Int32, GetAccessType(fieldDef)) }));

            fieldDef.Attributes &= ~FieldAttributes.FieldAccessMask;
            fieldDef.Attributes &= ~FieldAttributes.InitOnly;
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
