// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using Common.Contracts;
using Common.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Microsoft.MT.Common.Tokenization
{
    public static class ProcessTools
    {
        public static int RunCommand(
           string exe,
           string args,
           string stdoutPath, // must be null in this version
           string stderrPath, // may be null
           bool throwOnFailure = true,
           IEnumerable<KeyValuePair<string, string>> envirVariables = null)
        {
            Sanity.Requires(stdoutPath == null, "This reduced version of RunCommand() does not support stdout redirection");
            Logger.WriteLine($"executing command: {exe} {args}");
            using (TextWriter stderrWriter = stderrPath == null ? null : new StreamWriter(stderrPath, append: false, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true })
            using (var process = CreateProcess(exe, args, envirVariables, isPipe: false, stderr: stderrWriter))
            {
                process.WaitForExit();
                if (throwOnFailure && process.ExitCode != 0)
                    throw new IOException($"Exit code {process.ExitCode} was returned by external process: {exe} {args}");
                else
                    return process.ExitCode;
            }
        }


        static char[] k_ArgToCommandLineInvalidChars = Enumerable.Concat(from c in Enumerable.Range(0, (int)' ') select (char)c, new char[] { '"', '^' }).ToArray();
        /// <summary>
        /// escape an argument to a command line as needed in order to be parsed by CommandLineToArgv(), C++ CRT, or C#.
        /// Some characters that are tricky to handle consistently. For now, we simply forbid them.
        /// These include all control characters (0x00..0x1f), " (quotation marks inside string), and ^ (CMD shell escape).
        /// To handle " and ^ correctly, we may need additional context on whether this is run via CMD, and there is
        /// supposedly also a difference between CommandLineToArgV() and the C++ CRT (C# unknown) regarding sequences of double quotes.
        /// </summary>
        /// <param name="arg">Argument as the final string that the tool should receive, without escaping.</param>
        /// <returns>Escaped version of argument, or unmodified argument if no escaping is needed.</returns>
        static string ArgToCommandLine(string arg)
        {
            if (-1 != arg.IndexOfAny(k_ArgToCommandLineInvalidChars))
                throw new NotImplementedException($"ArgToCommandLine: presently cannot handle certain special characters (e.g. \" and ^) in: {arg}");
            if (!arg.Any() || arg.Contains(' '))  // space is the delimiter, so we must surround the arg by quotes
                return $"\"{arg}\"";
            else                    // otherwise, no need to escape (it would be OK to escape, but not escaping is better for log readability
                return arg;
        }
        /// <summary>
        /// convert an array of string arguments to a command line as needed in order to be parsed by CommandLineToArgv(), C++ CRT, or C#.
        /// </summary>
        public static string ArgsToCommandLine(IEnumerable<string> args)
            => string.Join(" ", from arg in args select ArgToCommandLine(arg));

        private static Process CreateProcess(string exe, string args,
                                             IEnumerable<KeyValuePair<string, string>> envirVariables, bool isPipe,
                                             TextWriter stderr)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ErrorDialog = false,
            };
            if (isPipe)
            {
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
            }
            if (stderr != null)
            {
                psi.RedirectStandardError = true;
                psi.StandardErrorEncoding = Encoding.UTF8; // @REVIEW: needed?
            }
            if (envirVariables != null)
                foreach (KeyValuePair<string, string> pair in envirVariables)
                    psi.EnvironmentVariables[pair.Key] = pair.Value;

            var process = new Process();
            process.StartInfo = psi;
            if (stderr != null)
                process.ErrorDataReceived += (sender, e) => { stderr.WriteLine(e.Data); };
            process.Start();
            if (stderr != null)
                process.BeginErrorReadLine();
            return process;
        }
    }
}
