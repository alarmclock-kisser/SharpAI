using SharpAI.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SharpAI.Runtime
{
    public partial class LlamaService
    {
        public List<string> GetAvailableBackends()
        {
            var backends = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (IsAssemblyAvailable("LLamaSharp.Backend.Cpu") || IsAssemblyAvailable("LLamaSharp.Backend.CPU"))
            {
                backends.Add("CPU");
            }

            if (IsAssemblyAvailable("LLamaSharp.Backend.Cuda12") || IsAssemblyAvailable("LLamaSharp.Backend.Cuda11") || IsAssemblyAvailable("LLamaSharp.Backend.Cuda"))
            {
                backends.Add("CUDA");
            }

            if (IsAssemblyAvailable("LLamaSharp.Backend.OpenCL") || IsAssemblyAvailable("LLamaSharp.Backend.OpenCl"))
            {
                backends.Add("OpenCL");
            }

            return backends.ToList();
        }

        private static bool IsAssemblyAvailable(string assemblyName)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in loaded)
            {
                if (string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            try
            {
                Assembly.Load(new AssemblyName(assemblyName));
                return true;
            }
            catch
            {
                return false;
            }
        }


        public async Task<Version?> GetCudaBackendVersionAsync()
        {
            try
            {
                Version? version = await Task.Run(() =>
                {
                    // Call CMD nvcc --version and parse output
                    System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "nvcc",
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (System.Diagnostics.Process? process = System.Diagnostics.Process.Start(startInfo))
                    {
                        string? output = process?.StandardOutput.ReadToEnd();
                        process?.WaitForExit();
                        // Parse the output to find the version number
                        // Example output line: "Cuda compilation tools, release 11.2, V11.2.152"
                        string[] lines = output?.Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries) ?? [];
                        foreach (string line in lines)
                        {
                            if (line.Contains("release"))
                            {
                                int releaseIndex = line.IndexOf("release") + "release".Length;
                                int commaIndex = line.IndexOf(",", releaseIndex);
                                if (commaIndex > releaseIndex)
                                {
                                    string versionString = line.Substring(releaseIndex, commaIndex - releaseIndex).Trim();
                                    if (Version.TryParse(versionString, out Version? cudaVersion))
                                    {
                                        return cudaVersion;
                                    }
                                }
                            }
                        }
                    }
                    return null;
                });

                return version;
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Error getting CUDA backend version: {ex.Message}");
                return null;
            }

        }
    }
}
