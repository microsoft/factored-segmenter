// This file contains a collection of utility functions. This is an extract
// from a larger library, reduced to what is actually used by this project.

using Common.Collections;
using Common.Collections.Extensions;
using Common.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Common.Collections.Extensions
{
    public static class StringExtensions
    {
        /// <summary>
        /// Convenience version of string.Join() that follows the Python syntax where the joiner is 'this'.
        /// </summary>
        public static string JoinItems<T>(this string separator, IEnumerable<T> items) => string.Join(separator, items);
    }
    public static class EnumerableExtensions
    {
        /// <summary>
        /// Create a sequence of overlapping pairs of the input.
        /// E.g. a b c d -> (a,b) (b,c) (c,d)
        /// </summary>
        /// <param name="sequence">Sequence of items. The sequence must have at least one element.</param>
        /// <returns>Sequence of bigrams</returns>
        public static IEnumerable<(T, T)> Bigrams<T>(this IEnumerable<T> sequence)
        {
            var seqEnum = sequence.GetEnumerator();
            bool movedNext = seqEnum.MoveNext();
            Sanity.Requires(movedNext, "Bigram() requires a non-empty input");
            T lastVal = seqEnum.Current;
            while (seqEnum.MoveNext())
            {
                T thisVal = seqEnum.Current;
                yield return (lastVal, thisVal);
                lastVal = thisVal;
            }
        }
    }
    public static class DictionaryExtensions
    {
        /// <summary>
        /// Same as Enumerable.SequenceEquals(), except that arguments may also be null.
        /// Amazingly, a.NullableSequenceEquals(b) works for a=null, thanks to the magic
        /// of extension methods.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="a">sequence or null</param>
        /// <param name="b">sequence or null</param>
        /// <returns>True if both args are null, or if both are non-null and sequences match</returns>
        public static bool NullableSequenceEquals<T>(this IEnumerable<T> a, IEnumerable<T> b)
        {
            return (a == null && b == null) ||
                   (a != null && b != null && Enumerable.SequenceEqual(a, b));
        }
    }
    public static class IOExtensions
    {
        /// <summary>
        /// Implements ReadLines() on the TextReader interface.
        /// </summary>
        public static IEnumerable<string> ReadLines(this TextReader textReader)
        {
            var line = textReader.ReadLine();
            while (line != null)
            {
                yield return line;
                line = textReader.ReadLine();
            }
        }
    }
}
namespace Common.Contracts
{
    public static class Sanity
    {
        public static bool Requires(bool condition, string errorMessage, params object[] args)
        {
            if (!condition)
            {
                if (args.Length == 0)
                    throw new ArgumentException(errorMessage);
                else
                    throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, errorMessage, args));
            }
            return true; // allows to use it in an expression
        }
    }
}
namespace Common.Utils
{
    public static class ProcessTool
    {
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
                psi.StandardInputEncoding = Encoding.UTF8;
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

        // @TODO: do we need IDisposable interface, so we can WaitForExit() for the process?
        public class ProcessPipe
        {
            public readonly Process process;
            public ProcessPipe(IList<string> argv, IEnumerable<KeyValuePair<string, string>> envirVariables = null) // UNIX-style argv array incl. exe itself
            {
                process = CreateProcess(argv.First(), ArgsToCommandLine(argv.Skip(1)), envirVariables: envirVariables, isPipe: true, stderr: null);
                process.StandardInput.AutoFlush = true;
            }
        }

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
    }
}
namespace Common.Utils
{
    public static class Logger
    {
        public static void WriteLine(string format, params object[] args) => Console.Error.WriteLine(format, args);

        public static void WriteLine(string s) => Console.Error.WriteLine(s);
    }
}
namespace Common.IO
{
    /// <summary>
    /// Contains static creator methods for various types of writers that will typically be used
    /// with AtomicFileWriter
    /// </summary>
    public static class AtomicFileWriter
    {
        /// <summary>
        /// move a file to a target location that gets deleted first if existing
        /// TODO: This seems to be duplicated about 20 times throughout the Solution; clean it up.
        /// This operation is faked to be "atomic" in that race conditions are handled that arise from concurrent attempts of doing the same thing on a parallel process.
        /// Note: if the source cannot be moved for whatever reason, but the target can be deleted, then this function will cause harm.
        /// TODO: the class name AtomicFileWriter does not seem fully appropriate for this function
        /// </summary>
        /// <param name="from"></param>
        /// <param name="to"></param>
        static void MoveReplace(string from, string to)
        {
            // This loop caters to the situation that two processes try to do this concurrently on
            // the same target path. The semantics should be that one of them wins. The special case
            // is that when this process deletes the target location, but then fails because a file at
            // the target path has magically reappeared. This must have been a concurrent process.
            // In this case, we just try again.
            while (true)
            {
                File.Delete(to);
                try
                {
                    File.Move(from, to);
                    return; // success
                }
                catch (IOException)
                {
                    if (!File.Exists(to)) // file not there: failed due to some other problem
                        throw;
                    // target file magically reappeared:
                    // This must be a concurrent thread. Just try again. If we cannot delete this new one, we will fail in Delete().
                }
            }
        }

        /// <summary>
        /// helper to save an object to disk via an intermediate tmp file and a lambda
        /// The caller must provide a lambda that accepts a (temporary) file name, and save to that.
        /// That temp file will then be atomically renamed into the target location.
        /// It is "atomic" in the sense that in case of an error, it will not leave a partially written file
        /// under the target name, and only overwrite a potentially existing one if the write operation succeeded.
        /// The outFilePath may be null. In that case, the SaveFunc() is called with null. This allows
        /// for nested Save() calls with multiple temp files, where some are optional.
        /// </summary>
        /// <param name="outFilePath">final output goes here</param>
        /// <param name="SaveFunc">lambda that creates a file (to which this function passes a temp path, which then gets renamed)</param>
        public static void Save(string outFilePath, Action<string> SaveFunc)
        {
            if (outFilePath == null)
            {
                SaveFunc(null);
                return;
            }
            string tmpPath = $"{outFilePath}.{Thread.CurrentThread.ManagedThreadId}$$";
            try
            {
                SaveFunc(tmpPath);
                MoveReplace(tmpPath, outFilePath);
            }
            catch
            {
                File.Delete(tmpPath); // best-effort cleanup (which may file e.g. in case of network disconnect)
                throw;
            }
        }
    }
}
namespace Common.Collections
{
    /// <summary>
    /// Wrapper around Dictionary with the following properties:
    /// (1) Dictionary is only allowed to grow to m_maxSize
    /// (2) Access is synchronized and read-write until the dictionary is full. 
    /// Once the dictionary is full it becomes read-only (subsequent adds are no-ops) and lock-free.
    /// The class was designed for use as a cache to store computations on key streams that are assumed to 
    /// exhibit Zipfian distribution.
    /// </summary>
    /// <typeparam name="K"></typeparam>
    /// <typeparam name="V"></typeparam>
    public class BoundedSizedLockingCache<K, V> //: IDictionary<K, V>
    {
        private object m_locker = new object();
        private int m_maxSize;
        private Dictionary<K, V> m_dict = new Dictionary<K, V>();
        volatile bool m_full = false;

        private void MaybeLock(Action act)
        {
            if (m_full)
            {
                act();
                return;
            }
            else
            {
                lock (m_locker)
                {
                    act();
                }
            }
        }

        private RetT MaybeLock<RetT>(Func<RetT> func)
        {
            if (m_full)
            {
                return func();
            }
            lock (m_locker)
            {
                return func();
            }
        }

        private void MaybeSetFull()
        {
            MaybeLock(() => { if (m_dict.Count >= m_maxSize) { m_full = true; } });
        }

        /// <summary>
        /// Create a cache 
        /// </summary>
        /// <param name="maxSize">Maximum size of cache. Setting to 0 effectively disables the cache.</param>
        public BoundedSizedLockingCache(int maxSize)
        {
            m_maxSize = maxSize;
            MaybeSetFull();
        }
        /// <summary>
        /// If the dictionary has room, add key and value. Otherwise this is a no-op.
        /// </summary>
        public void Add(K key, V value)
        {
            if (m_full)
                return;
        
            MaybeLock(() =>
            {
                if (!m_dict.ContainsKey(key))
                    m_dict.Add(key, value);
                MaybeSetFull();
            });
        }
        public bool TryGetValue(K key, out V value)
        {
            if (m_full)
            {
                return m_dict.TryGetValue(key, out value);
            }
            lock (m_locker)
            {
        
                return m_dict.TryGetValue(key, out value);
            }
        }
    }
}
namespace Microsoft.MT.Common.Tokenization
{
    public static class CachedFunction
    {
        /// <summary>
        /// If an entry exists in the cache for key, return it. Otherwise, call unary function func and add it to cache.
        /// </summary>
        public static int[] Memoize(BoundedSizedLockingCache<string, int[]> cache, string key, Func<string, int[]> func)
        {
            if (cache.TryGetValue(key, out var ret))
                return ret;
            ret = func(key);
            cache.Add(key, ret);
            return ret;
        }
    }
}
