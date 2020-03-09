// Wrapper around the SentencePiece runtime library.
// This is currently emulated by a process-based interface,
// until a real P/invoke implementation is completed.

using System;
using System.IO;
using System.Text;
using System.Collections.Concurrent;
using static Common.Utils.ProcessTool;
using Common.Contracts;
using Common.Collections.Extensions;
using System.Linq;
using Common.Utils;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Microsoft.MT.Segmentation
{
    public class SentencePieceManaged // : IDisposable
    {
        static readonly string spmBinaryDirPathLinux = "/usr/local/bin/";
        static readonly string spmBinaryDirPathWindows = @"c:\work\mtmain\target\Retail\amd64\Tokenization\";

        public static string SpmBinaryDirPath =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? spmBinaryDirPathWindows : spmBinaryDirPathLinux;

        HashSet<string> m_vocabulary;
        readonly string m_tempModelPath;
        readonly string m_tempVocabPath;
        readonly ConcurrentQueue<ProcessPipe> m_serverPool;
        public SentencePieceManaged()
        {
            m_vocabulary = null;
            m_tempModelPath = Path.GetTempFileName();
            m_tempVocabPath = Path.GetTempFileName();
            m_serverPool = new ConcurrentQueue<ProcessPipe>(); // pool of SPM helper processes. We need multiple if running multi-threaded.
        }

        // This is the only interface into SPM used by FactoredSegmenter.
        // It determines the split points where SPM would split.
        // @TODO: change return type to IList type, which will save one operation in this build, while costing nothing in MTMAIN
        public int[] GetSplitPoints(string segmentMe)
        {
            if (segmentMe.Length <= 1) // nothing to split. This includes space, which is SPM's break symbol, and should not be sent.
                return null;
            // obtain a server process if available, or create a new one if all are in use
            if (!m_serverPool.TryDequeue(out var processPipe))
            {
                var argv = new List<string> { SpmBinaryDirPath + "spm_encode", "--model", m_tempModelPath };
                if (m_vocabulary != null)
                    argv.AddRange(new List<string> { "--vocabulary", m_tempVocabPath });
                Logger.WriteLine($"starting SentencePiece instance as: {" ".JoinItems(argv)}");
                processPipe = new ProcessPipe(argv, envirVariables: new Dictionary<string, string> { { "LC_ALL", "en_US.UTF-8" } });
                // @TODO: do we need the environment variable for spm_encode?
            }
            //Logger.WriteLine($"SPM-encoding word {segmentMe}");
            processPipe.process.StandardInput.WriteLine(segmentMe); // @TODO: how do we know/ensure this is UTF-8?
            var encodedWord = processPipe.process.StandardOutput.ReadLine();
            Sanity.Requires(encodedWord != null, "spm_encode unexpectedly terminated");
            // return the process back into the pool
            m_serverPool.Enqueue(processPipe);

            var pieces = encodedWord.Split(' ', options: StringSplitOptions.RemoveEmptyEntries);
            if ("".JoinItems(pieces) != segmentMe)
            {
                Logger.WriteLine($"ignoring word: SentencePiece did not just split the word ('{segmentMe}', -> '{" ".JoinItems(pieces)}')");
                return null;
            }

            // create array of segmentation points
            // E.g. if "abcde" got broken into "ab cde", then we return the split points (0, 2, 5).
            // This code handles the special case of OOV pieces.
            // E.g. if there is no '+' in the SentencePiece vocab, then spm_encode will keep
            // it as '++++'. We must break those up into individual pieces.
            List<int> res = null; // (created lazily)
            int n = 0; // accumulator for split points
            for (int i = 0; i < pieces.Length; i++)
            {
                var piece = pieces[i];
                if (m_vocabulary == null || m_vocabulary.Contains(piece))
                {
                    n += piece.Length;
                    if (n < segmentMe.Length || res != null) // (in the frequent special case of an unbroken single token, we return null for efficiency)
                    {
                        if (res == null)
                            res = new List<int> { 0, n };
                        else
                            res.Add(n);
                    }
                }
                else // special case: OOV. Break at each character.
                    for (int j = 0; j < piece.Length; j++)
                    {
                        bool skipLow = char.IsHighSurrogate(piece[j]) && j + 2 <= piece.Length;
                        n += skipLow ? 2 : 1; // length of this piece is 1 Unicode character. Surrogate pairs are 2 characters in C#'s UCS-2 encoding.
                        if (n < segmentMe.Length || res != null)
                        {
                            if (res == null)
                                res = new List<int> { 0, n };
                            else
                                res.Add(n);
                        }
                    }
            }
            return res?.ToArray();
        }
        public void LoadModel(string loadMe, string[] vocabulary)
        {
            m_vocabulary = vocabulary?.ToHashSet();
            // save to file during the lifetime of this object
            File.WriteAllBytes(m_tempModelPath, File.ReadAllBytes(loadMe));
            if (vocabulary != null)
                File.WriteAllLines(m_tempVocabPath, vocabulary, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        public string[] Segment(string segmentMe) { throw new NotImplementedException("Segment() not implemented in this build."); }
        public string Unsegment(string[] unsegmentMe) { throw new NotImplementedException("Unsegment() not implemented in this build."); }

        //public static bool IsHighSurrogate(char c) { return true; }
        //public sealed override void Dispose()
        //{
        //    //Dispose(true);
        //    GC.SuppressFinalize(this);
        //}
        //[HandleProcessCorruptedStateExceptions]
        //protected virtual void Dispose(bool A_0) { }
    }
}
