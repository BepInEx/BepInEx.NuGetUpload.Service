// Port from https://github.com/BepInEx/BepInEx.AssemblyPublicizer/tree/master since these types are internal
// TODO: Maybe AssemblyPublicizer should expose some helper to just read the assembly (or some kind of preprocessor)

using System;
using System.Collections.Generic;
using System.IO;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Builder;
using AsmResolver.DotNet.Serialized;
using AsmResolver.IO;
using AsmResolver.PE;
using AsmResolver.PE.DotNet.Builder;

namespace NuGetUpload.Utils;

internal static class AsmResolverHelper
{
    public static AssemblyDefinition FromFile(string filePath)
    {
        return AssemblyDefinition.FromImage(PEImage.FromFile(filePath),
            new ModuleReaderParameters(FatalThrowErrorListener.Instance));
    }

    public static void FatalWrite(this ModuleDefinition module, string filePath)
    {
        var result = new ManagedPEImageBuilder().CreateImage(module);
        if (result.HasFailed)
            throw new AggregateException("Construction of the PE image failed with one or more errors.",
                result.DiagnosticBag.Exceptions);

        using var fileStream = File.Create(filePath);
        new ManagedPEFileBuilder().CreateFile(result.ConstructedImage).Write(new BinaryStreamWriter(fileStream));
    }

    private sealed class FatalThrowErrorListener : IErrorListener
    {
        public static FatalThrowErrorListener Instance { get; } = new();

        private IList<Exception> Exceptions { get; } = new List<Exception>();

        public void MarkAsFatal()
        {
            throw new AggregateException(Exceptions);
        }

        public void RegisterException(Exception exception)
        {
            Exceptions.Add(exception);
        }
    }
}

internal class NoopAssemblyResolver : IAssemblyResolver
{
    internal static NoopAssemblyResolver Instance { get; } = new();

    public AssemblyDefinition Resolve(AssemblyDescriptor assembly)
    {
        return null;
    }

    public void AddToCache(AssemblyDescriptor descriptor, AssemblyDefinition definition)
    {
    }

    public bool RemoveFromCache(AssemblyDescriptor descriptor)
    {
        return false;
    }

    public bool HasCached(AssemblyDescriptor descriptor)
    {
        return false;
    }

    public void ClearCache()
    {
    }
}