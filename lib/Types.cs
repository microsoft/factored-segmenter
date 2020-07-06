// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

// This file contains a collection of enums, data structures, and interfaces
// referenced in FactoredSegmenter. These have been extracted from a larger library.

using System;
using System.Collections.Generic;

namespace Common.MT.Segments
{
    public class AlignmentLink : IComparable<AlignmentLink>, IEquatable<AlignmentLink>
    {
        public int SourceIndex { get; }
        public int TargetIndex { get; }
        public float Confidence { get; }
        public override int GetHashCode() => SourceIndex ^ (TargetIndex << 16);
        public bool Equals(AlignmentLink that) => that != null && this.SourceIndex == that.SourceIndex && this.TargetIndex == that.TargetIndex;
        public override string ToString() => $"{SourceIndex}:{TargetIndex}";
        public int CompareTo(AlignmentLink other)
        {
            int c1 = SourceIndex.CompareTo(other.SourceIndex);
            if (c1 != 0)
                return c1;
            return TargetIndex.CompareTo(other.TargetIndex);
        }
    }
    public class Alignment
    {
        public List<AlignmentLink> Links { get; private set; }
        public Alignment InsertMissingTarget(int sourceIndex, int targetIndex)
            => throw new NotImplementedException("InsertMissingTarget is not supported");
        public int GetTargetIndexToInsert(int originalSrcIndex) => -1;
        public override string ToString() => string.Join(" ", Links);
    }
}
namespace Microsoft.MT.TextSegmentation.SpanFinder
{
    public enum AnnotatedSpanClassType
    {
        PhraseFix
    }
    public enum AnnotatedSpanInstructions  // note: ignored in standalone build
    {
        ForceDecodeAs,
        EncodeAsIf
    }
    public class AnnotatedSpan
    {
        public int StartIndex { get; private set; } // coordinates into the raw source string
        public int Length { get; private set; }
        /// <summary>
        /// If given, then Encode() will pretend that the character range was this string instead of the original.
        /// Casing and word/continuous-script factors are derived as if these characters were in the original.
        /// </summary>
        public string EncodeAsIf { get; private set; }
        /// <summary>
        /// If given, then Decode() will decode this token as the given string. Use this for PhraseFix.
        /// If not given, then Decode() will reproduce the original character string. Used internally for unencodable characters.
        /// This requires a class type. @TODO: In the future, it can also be a parenthesized pass-through (A|B).
        /// @BUGBUG: For now, we do not handle casing; DecodeAs is just applied as-is. Need to decide what to do here.
        /// </summary>
        public string DecodeAs { get; private set; }
        /// <summary>
        /// If given, this is the class type to use to represent this token in Marian.
        /// The reason to use different class types is that different
        /// classes may occur in different grammatical contexts (e.g. PhraseFix vs. Url).
        /// </summary>
        public AnnotatedSpanClassType? ClassType { get; private set; } // if non-null, then use this class token
        public AnnotatedSpan(
            int startIndex,
            int length,
            AnnotatedSpanClassType? classType,
            AnnotatedSpanInstructions instructions = AnnotatedSpanInstructions.ForceDecodeAs,  // note: ignored in standalone build
            string decodeAs = null,
            string encodeAsIf = null)
        {
            StartIndex = startIndex;
            Length = length;
            ClassType = classType;
            DecodeAs = decodeAs;
            EncodeAsIf = encodeAsIf;
        }
    }
}
namespace Microsoft.MT.Common.Tokenization
{
    public enum SegmenterKind
    {
        FactoredSegmenter,
        SentencePiece, // (not actually supported in this library)
        Unknown
    }
    public interface ISegmenterConfig { }
    public interface ISentencePieceConfig : ISegmenterConfig { }
    public interface IFactoredSegmenterConfig : ISegmenterConfig { }
    public class SegmenterConfigBase { }
    public abstract class SegmenterTrainConfigBase : SegmenterConfigBase
    {
        /// <summary>
        /// Maximum size of sentences to train sentence pieces
        /// </summary>
        public abstract int? TrainingSentenceSize { get; set; }
    }
    public class SegmenterEncodeConfigBase : SegmenterConfigBase { }
    public class SegmenterDecodeConfigBase : SegmenterConfigBase { }
    public class ProcessedToken
    {
        public static ProcessedToken CreateRegularToken(string sourceWord, List<string> origSource = null, int rawCharStart = -1, int rawCharLength = -1)
            => throw new NotImplementedException("The ProcessedToken interface is not available in this build.");
    }
}
