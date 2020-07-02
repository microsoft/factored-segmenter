using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Segmentation
{
    public class SentencePieceManaged
    {
        private readonly IntPtr model;

        private static class NativeMethods
        {
            private const string DllName = "SentencePieceInterop";
            [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr LoadModel(String modelPath,
                [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 2)]String[] vocab, ulong vocabSize);

            [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
            public static extern int EncodeAsIds(IntPtr model, string word, 
                [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.I4, SizeParamIndex = 3)]int[] pieceIdBuffer, ulong pieceIdBufferSize);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int UCS2LengthOfPieceId(IntPtr model, int pieceId);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void UnloadModel(IntPtr model);

        }


        public SentencePieceManaged(String modelPath, string[] vocab)
        {
            ulong vocabLength = (ulong?)vocab?.Length ?? 0UL;
            IntPtr local = NativeMethods.LoadModel(modelPath, vocab, (ulong) vocabLength);
            if (local == IntPtr.Zero)
                throw new ArgumentNullException($"Could not load model file from path {modelPath}");

            this.model = local;
        }

        ~SentencePieceManaged()
        {
            if (model != IntPtr.Zero)
            {
                NativeMethods.UnloadModel(this.model);
            }
        }

        /// <summary>
        /// This function splits a string (typically a word) into pieces. Instead of returning the pieces, it returns the indices of the split points as an array of integers (including 0 and N).
        /// In the frequent case that nothing is split, we instead return null to save a memory allocation.
        /// </summary>
        /// <param name="segment">The word to split</param>
        /// <returns>Array representing indices where to split the word, including 0 and N, or null which maps to [0,N]</returns>
        public int[] GetSplitPoints(String segment)
        {
            if (String.IsNullOrEmpty(segment) || segment.Length == 1)
                return null;
            int[] pieceIds = new int[segment.Length];
            // break string using SentencePiece library
            int size = NativeMethods.EncodeAsIds(model, segment, pieceIds, (ulong)pieceIds.Length); ;
            if(size < 0)
            {
                throw new InvalidOperationException("SentencePiece returned a negative size array");
            } 

            if (size == 1)
            {
                int length = NativeMethods.UCS2LengthOfPieceId(this.model, pieceIds[0]);
                // if it's length 1 and not an UNK token, we return null
                if (length != -1)
                    return null;
            }
            
            // create the array of offsets, by aggregating the lengths of all pieces
            int segmentSize = segment.Length;
            List<int> cutList = new List<int>();
            cutList.Add(0); // 0 is always included in the cut-list
            
            bool done = false;
            while (!done) // retry loop used in case of unencodable characters
            {
                done = true;
                for (int i = 0; i < size; i++)
                {
                    if (cutList.Last() >= segmentSize) // logic error
                        throw new InvalidOperationException($"Unexpectedly hit the end while splitting {segment}");
                    int pieceId = pieceIds[i];
                    int pieceLength = NativeMethods.UCS2LengthOfPieceId(this.model, pieceId);
                    // handle unknown character
                    // Unfortunately, SPM just returns a single <unk> token for any sequence of unencodable
                    // characters, without telling us how many source characters it is made up of.
                    // To work around this, we split off the first char of the <unk> token, but then
                    // call Encode() again with the remaining string. If the <unk> consisted of
                    // more than one unencodable, the same mechanism will then kick in to split off the next
                    // char, call Encode() again etc. This has square complexity w.r.t. string length,
                    // but sequences are short, and this does not happen too frequently.
                    if (pieceLength == -1)                  // -1 indicates one or more unknown characters
                    {
                        bool skipLow = Char.IsHighSurrogate(segment, cutList.Last()) && cutList.Last() + 2 <= segmentSize;
                        cutList.Add(cutList.Last() + 1 + (skipLow ? 1 : 0)); // consume it (skip two if surrogate pair)
                        if (cutList.Last() == segmentSize)             // none left
                            ;
                        else if (cutList.Last() + 1 == segmentSize)    // single char left
                            cutList.Add(segmentSize);
                        else                                           // more left: go again with remainder
                        {
                            // find the substring from the last index that had a length to the end
                            String copySegment = segment.Substring(cutList.Last());
                            size = NativeMethods.EncodeAsIds(model, copySegment, pieceIds, (ulong)pieceIds.Length);
                            if(size < 0)
                                throw new InvalidOperationException("Substring should use less space than original");
                            done = false;
                        }

                        // if we found an unk, break the current loop, and start a new loop over, if there are any characters left
                        break;
                    }
                    // regular case
                    else
                    {
                        cutList.Add(cutList.Last() + pieceLength);
                    }
                }
            }

            if (cutList.Last() != segmentSize)
                throw new InvalidOperationException("Sentence pieces do not reconstruct original string??");
            return cutList.ToArray();
        }

        public string[] Segment(String line)
        {
            throw new NotImplementedException();
        }

        public String Unsegment(string[] pieces)
        {
            throw new NotImplementedException();
        }
    }
}
