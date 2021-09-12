using System;
using System.IO;

namespace NuGetUpload.Utils
{
    public sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; }
        
        public string Name { get; }
        
        public TemporaryFolder(string basePath)
        {
            Name = System.IO.Path.GetRandomFileName();
            Path = System.IO.Path.Combine(basePath, Name);
            Directory.CreateDirectory(Path);
        }
        
        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }
}
