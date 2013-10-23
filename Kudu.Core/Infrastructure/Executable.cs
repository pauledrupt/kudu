using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Kudu.Contracts.Tracing;
using Kudu.Core.Deployment;
using Kudu.Core.Tracing;

namespace Kudu.Core.Infrastructure
{
    public class Executable : Kudu.Core.Infrastructure.IExecutable
    {
        public Executable(string path, string workingDirectory, TimeSpan idleTimeout)
        {
            Path = path;
            WorkingDirectory = workingDirectory;
            EnvironmentVariables = new Dictionary<string, string>();
            Encoding = Encoding.UTF8;
            IdleTimeout = idleTimeout;
        }

        public bool IsAvailable
        {
            get
            {
                return File.Exists(Path);
            }
        }

        public void SetHomePath(IEnvironment environment)
        {
            if (!String.IsNullOrEmpty(environment.RootPath))
            {
                SetHomePath(environment.RootPath);
            }
        }

        public void SetHomePath(string homePath)
        {
            // SSH requires HOME directory and applies to git, npm and (CustomBuilder) cmd
            // Don't set it if it's already set, as would be the case in Azure
            if (String.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("HOME")))
            {
                EnvironmentVariables["HOME"] = homePath;
            }

            EnvironmentVariables["HOMEDRIVE"] = homePath.Substring(0, homePath.IndexOf(':') + 1);
            EnvironmentVariables["HOMEPATH"] = homePath.Substring(homePath.IndexOf(':') + 1);
        }

        public string WorkingDirectory { get; private set; }

        public string Path { get; private set; }

        public IDictionary<string, string> EnvironmentVariables { get; set; }

        public Encoding Encoding { get; set; }

        public TimeSpan IdleTimeout { get; private set; }

        public Tuple<string, string> Execute(string arguments, params object[] args)
        {
            return Execute(NullTracer.Instance, arguments, args);
        }

        public Tuple<string, string> Execute(ITracer tracer, string arguments, params object[] args)
        {
            using (MemoryStream outputStream = new MemoryStream(),
                                errorStream = new MemoryStream())
            {
                Execute(tracer, input: null, output: outputStream, error: errorStream, arguments: arguments, args: args);

                return Tuple.Create(ReadAsString(outputStream),
                                    ReadAsString(errorStream));
            }
        }

        public void Execute(ITracer tracer, Stream input, Stream output, string arguments, params object[] args)
        {
            using (var errorStream = new MemoryStream())
            {
                Execute(tracer, input: input, output: output, error: errorStream, arguments: arguments, args: args);
            }
        }

        private void Execute(ITracer tracer, Stream input, Stream output, MemoryStream error, string arguments, params object[] args)
        {
            using (GetProcessStep(tracer, arguments, args))
            {
                var process = CreateProcess(arguments, args);
                process.Start();

                var idleManager = new IdleManager(IdleTimeout, tracer, output);
                
                var tasks = new List<Task>();
                if (input != null)
                {
                    // Copy into the input stream, and close it to tell the exe it can process it
                    tasks.Add(Task.Run(async() =>
                        await CopyStream(tracer, idleManager, input, process.StandardInput.BaseStream, closeAfterCopy: true)));
                }

                // Copy the exe's output into the output stream
                tasks.Add(Task.Run(async () =>
                    await CopyStream(tracer, idleManager, process.StandardOutput.BaseStream, output, closeAfterCopy: false)));

                tasks.Add(Task.Run(async() => 
                    await CopyStream(tracer, idleManager, process.StandardError.BaseStream, error, closeAfterCopy: false)));

                idleManager.WaitForExit(process);

                // Wait for the output operation to be complete
                Task.WaitAll(tasks.ToArray());
                
                tracer.TraceProcessExitCode(process);

                if (process.ExitCode != 0)
                {
                    var errorString = ReadAsString(error);
                    throw new CommandLineException(Path, process.StartInfo.Arguments, errorString)
                    {
                        ExitCode = process.ExitCode,
                        Error = errorString
                    };
                }
            }
        }

        private static async Task CopyStream(ITracer tracer, IdleManager idleManager, Stream from, Stream to, bool closeAfterCopy)
        {
            try
            {
                byte[] bytes = new byte[1024];
                int read = 0;
                bool writeError = false;
                while ((read = await from.ReadAsync(bytes, 0, bytes.Length)) != 0)
                {
                    idleManager.UpdateActivity();
                    try
                    {
                        if (!writeError)
                        {
                            await to.WriteAsync(bytes, 0, read);
                        }
                    }
                    catch (Exception ex)
                    {
                        writeError = true;
                        tracer.TraceError(ex);
                    }
                }

                idleManager.UpdateActivity();
                if (closeAfterCopy)
                {
                    to.Close();
                }
            }
            catch (Exception ex)
            {
                tracer.TraceError(ex);
            }
        }

        public Tuple<string, string> ExecuteWithProgressWriter(ILogger logger, ITracer tracer, string arguments, params object[] args)
        {
            try
            {
                using (var writer = new ProgressWriter())
                {
                    return Execute(tracer,
                                   output =>
                                   {
                                       writer.WriteOutLine(output);
                                       logger.Log(output);
                                       return true;
                                   },
                                   error =>
                                   {
                                       writer.WriteErrorLine(error);
                                       logger.Log(error, LogEntryType.Warning);
                                       return true;
                                   },
                                   Console.OutputEncoding,
                                   arguments,
                                   args);
                }
            }
            catch (CommandLineException exception)
            {
                // in case of failure without stderr, we log error explicitly
                if (String.IsNullOrEmpty(exception.Error))
                {
                    logger.Log(exception);
                }

                throw;
            }
            catch (Exception exception)
            {
                // in case of other failure, we log error explicitly
                logger.Log(exception);

                throw;
            }
        }

        public Tuple<string, string> Execute(ITracer tracer, Func<string, bool> onWriteOutput, Func<string, bool> onWriteError, Encoding encoding, string arguments, params object[] args)
        {
            using (GetProcessStep(tracer, arguments, args))
            {
                Process process = CreateProcess(arguments, args);
                process.EnableRaisingEvents = true;

                var errorBuffer = new StringBuilder();
                var outputBuffer = new StringBuilder();

                var idleManager = new IdleManager(IdleTimeout, tracer);
                process.OutputDataReceived += (sender, e) =>
                {
                    idleManager.UpdateActivity();
                    if (e.Data != null)
                    {
                        if (onWriteOutput(e.Data))
                        {
                            outputBuffer.AppendLine(Encoding.UTF8.GetString(encoding.GetBytes(e.Data)));
                        }
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    idleManager.UpdateActivity();
                    if (e.Data != null)
                    {
                        if (onWriteError(e.Data))
                        {
                            errorBuffer.AppendLine(Encoding.UTF8.GetString(encoding.GetBytes(e.Data)));
                        }
                    }
                };

                process.Start();

                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                try
                {
                    idleManager.WaitForExit(process);
                }
                catch (Exception ex)
                {
                    onWriteError(ex.Message);
                    throw;
                }

                tracer.TraceProcessExitCode(process);

                string output = outputBuffer.ToString().Trim();
                string error = errorBuffer.ToString().Trim();

                if (process.ExitCode != 0)
                {
                    string text = String.IsNullOrEmpty(error) ? output : error;

                    throw new CommandLineException(Path, process.StartInfo.Arguments, text)
                    {
                        ExitCode = process.ExitCode,
                        Output = output,
                        Error = error
                    };
                }

                return Tuple.Create(output, error);
            }
        }

        private IDisposable GetProcessStep(ITracer tracer, string arguments, object[] args)
        {
            return tracer.Step("Executing external process", new Dictionary<string, string>
            {
                { "type", "process" },
                { "path", System.IO.Path.GetFileName(Path) },
                { "arguments", String.Format(arguments, args) }
            });
        }

        internal Process CreateProcess(string arguments, object[] args)
        {
            return CreateProcess(String.Format(arguments, args));
        }

        internal Process CreateProcess(string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path,
                WorkingDirectory = WorkingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                ErrorDialog = false,
                Arguments = arguments
            };

            if (Encoding != null)
            {
                psi.StandardOutputEncoding = Encoding;
                psi.StandardErrorEncoding = Encoding;
            }

            foreach (var pair in EnvironmentVariables)
            {
                psi.EnvironmentVariables[pair.Key] = pair.Value;
            }

            var process = new Process()
            {
                StartInfo = psi
            };

            return process;
        }

        private static string ReadAsString(MemoryStream stream)
        {
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
