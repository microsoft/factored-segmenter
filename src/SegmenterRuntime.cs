using Common.Collections.Extensions;
using Common.MT.Segments;
using Common.Text;
using Microsoft.MT.TextSegmentation.SpanFinder;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Microsoft.MT.Common.Tokenization.Segmenter
{
    public class SegmenterCoderConfig
    {
        public SegmenterKind SegmenterKind { get; set; }
        public string ModelPath { get; set; }

        // The Equals() function is for the parallel coder so that it can  determine whether
        // source and target configs are the same. If they are, the parallel coder will only
        // instantiate one segmenter and use it for both source and target.
        public override bool Equals(object obj)
        {
            return
                obj is SegmenterCoderConfig other &&
                SegmenterKind == other.SegmenterKind && ModelPath == other.ModelPath;
        }
        public override int GetHashCode() { return ModelPath.GetHashCode(); }
    }

    /// <summary>
    /// A reference to a segment of raw source text, as used in DecodedSegment.SourceLink
    /// </summary>
    public class EncodedSegmentReference
    {
        public string RawSourceText; // full raw source string   --@TODO: make private if not actually needed public
        public int StartIndex;       // character coordinates of source token in the raw source string
        public int Length;
        public bool IsWordTokenStart, IsWordTokenEnd;
        public bool IsSpacingWordStart, IsSpacingWordEnd;
        public string SurfaceForm => RawSourceText.Substring(StartIndex, Length);
        public override bool Equals(object obj)
        {
            return
                obj is EncodedSegmentReference other &&
                RawSourceText == other.RawSourceText && StartIndex == other.StartIndex && Length == other.Length &&
                IsWordTokenStart == other.IsWordTokenStart && IsWordTokenEnd == other.IsWordTokenEnd &&
                IsSpacingWordStart == other.IsSpacingWordStart && IsSpacingWordEnd == other.IsSpacingWordEnd;
        }
        public override int GetHashCode() { return RawSourceText.GetHashCode(); }
        // for debugging
        public override string ToString() => SurfaceForm;
    }

    /// <summary>
    /// The decoder outputs one of these for each Marian token, and additional ones for reconstructed spaces.
    /// The encoder uses this to return the segmentation of the source string.
    /// </summary>
    public struct DecodedSegment : IEquatable<DecodedSegment>
    {
        public readonly string SurfaceForm; // final plain-text form
        public readonly bool IsWordTokenStart, IsWordTokenEnd;
        public readonly bool IsSpacingWordStart, IsSpacingWordEnd;

        public struct SourceLink : IEquatable<SourceLink>// for representing alignment information
        {
            public EncodedSegmentReference SourceSegment; // contains the character alignment
            public float Confidence; // @TODO: unit? prob or log prob?
            public bool Equals(SourceLink other)
            {
                return
                    ((SourceSegment == null) == (other.SourceSegment == null) ||
                     (SourceSegment != null) && SourceSegment.Equals(other.SourceSegment)) &&
                    Confidence == other.Confidence;
            }
        }
        public readonly List<SourceLink> SourceAlignment; // character range(s) (and confidence) of original source string(s)

        /// <summary>
        /// True if this segment's surface string was set using the DecodeAs mechanism (e.g. for phrasefix, urls, etc)
        /// </summary>
        public bool IsForceDecode { get; set; }
        public DecodedSegment(string surfaceForm, bool isWordTokenStart, bool isWordTokenEnd, List<SourceLink> sourceLinks, bool isForceDecode, bool isSpacingWordStart, bool isSpacingWordEnd)
        {
            SurfaceForm = surfaceForm;
            IsWordTokenStart = isWordTokenStart;
            IsWordTokenEnd = isWordTokenEnd;
            SourceAlignment = sourceLinks;
            IsForceDecode = isForceDecode;
            IsSpacingWordStart = isSpacingWordStart;
            IsSpacingWordEnd = isSpacingWordEnd;
        }
        public bool Equals(DecodedSegment other)
        {
            return SurfaceForm == other.SurfaceForm && IsWordTokenStart == other.IsWordTokenStart && IsWordTokenEnd == other.IsWordTokenEnd &&
                   SourceAlignment.NullableSequenceEquals(other.SourceAlignment) && IsForceDecode == other.IsForceDecode &&
                   IsSpacingWordStart == other.IsSpacingWordStart && IsSpacingWordEnd == other.IsSpacingWordEnd;
        }

        // for debugging
        public override string ToString() => SurfaceForm;

        /// <summary>
        /// Clone this object, but with a replaced surface form. This is inteded to be used for making modifications
        /// to surface forms during postprocessing without disrupting alignment or word boundary flags. Examples are
        /// ensuring that all question marks in Chinese are full width ('？' rather than '?') or that the newer and
        /// more correct form of certain T/S diacritics for Romanian ('Ș' rather than 'Ş').
        /// 
        /// Using this has the potential to create a situation where some of the assumptions associated with word boundary
        /// flags are violated. For example, IsSpacingWordStart/End can be true between two continuous script segments (e.g.
        /// Japanese or Chinese), but would not be true between two spacing script segments in general (e.g. Latin or Cyrillic). 
        /// If we replace surface forms for two consecutive Japanese segments with Latin strings, we would then have a
        /// two consecutive Latin segments with IsSpacingWord* set to true, allowing tags and character alignment boundaries
        /// to be placed at that boundary.
        /// 
        /// For these reasons, it is much safer to use this to change surface forms within like segment classes (e.g.
        /// punctuation, characters within a script, etc).
        /// 
        /// For example, if we wanted to clone a DecodedSegment, questionMarkSeg, whose surface form was "?", but
        /// wanted to the clone to have a full width question mark, we could use the following code:
        /// var fullWidthSeg = questionMarkSeg.WithSurfaceForm("？");
        /// </summary>
        /// <param name="newSurfaceForm">A new surface form that will given to the clone</param>
        /// <returns>A clone of this object, but with surface form replaced by specified argument</returns>
        public DecodedSegment WithSurfaceForm(string newSurfaceForm)
        {
            return new DecodedSegment(
                surfaceForm: newSurfaceForm,
                isWordTokenStart: IsWordTokenStart,
                isWordTokenEnd: IsWordTokenEnd,
                sourceLinks: SourceAlignment,
                isForceDecode: IsForceDecode,
                isSpacingWordStart: IsSpacingWordStart,
                isSpacingWordEnd: IsSpacingWordEnd);
        }
    }

    /// <summary>
    /// This is an opaque object returned by the Segmenter.Encode that gives instructions for how to replace word classed tokens 
    /// (e.g. phrasefix) at Segmenter.Decode time.
    /// </summary>
    public interface IDecoderPackage { } // @TODO: find a better name

    /// <summary>
    /// The result of Encode(), which consists of
    ///  - tokens in their serialized string form, for use by Marian NMT training
    ///  - segmentation information, for use in alignment
    /// </summary>
    public abstract class IEncoded
    {
        /// <summary>
        /// The original source line of raw plain text that was to be encoded.
        /// </summary>
        public abstract string OriginalSourceText { get; }
        /// <summary>
        /// Source line segments that correspond to the encoded tokens.
        /// Each token carries additional word-boundary information.
        /// Tokens are in left-to-right order, but possibly with repeats, and may have gaps.
        /// This array allows to find the set of segmentation boundaries, for example for
        /// training an alignment model or tag manipulations.
        /// Do NOT, however, use this to reconstruct the original source line, because:
        ///  - spaces are not included (since they get elided in encoding)
        ///  - any replaced ranges (phrase fixes, EncodeAsIf) only have their outer boundaries
        ///  - if a replaced range gets SentencePiece'd, then we get multiple tokens that each
        ///    span the full original replaced region
        /// Examples:
        ///  - "abc defg hi" with defg inline-phrase-fixed to XYZ, with SPM-splits de fg and XY Z.
        ///    Tokens will be something like "abc (( de fg || XY Z )) hi" (factors not shown).
        ///    Resulting source text segments will be "abc '' '' '' '' defg defg '' hi".
        ///    (@TODO: A future version may retain de and fg as well.)
        /// </summary>
        public abstract EncodedSegmentReference[] OriginalSourceTextSegments { get; }
        /// <summary>
        /// The encoding result expressed as a sequence of ProcessToken items.
        /// </summary>
        public abstract List<ProcessedToken> ProcessedTokens { get; }
        /// <summary>
        /// The encoding result expressed as a sequence of tokens in their serialized (encoded) form.
        /// </summary>
        public abstract IEnumerable<string> TokenStrings { get; }
        /// <summary>
        /// The encoding result expressed as a sequence of tokens in their serialized (encoded) form.
        /// This is different from TokenStrings() since e.g. for FactoredSegmenter, the aligner
        /// should not receive factors.
        /// </summary>
        public abstract IEnumerable<string> TokenStringsForAligner { get; }
        /// <summary>
        /// Number of tokens. All properties above except OriginalSourceTextSegments return this many items.
        /// </summary>
        public abstract int Count { get; }
        /// <summary>
        /// The original source sentence annotations that was passed to Encode().
        /// </summary>
        public abstract Dictionary<string, string> OriginalSourceSentenceAnnotations { get; }
        /// <summary>
        /// The result expressed as a single text line; meant for debugger visualization only.
        /// </summary>
        public override string ToString() => " ".JoinItems(TokenStrings);
        /// <summary>
        /// This property holds an opaque package of information that should be passed on to
        /// the Decode() function.
        /// </summary>
        public abstract IDecoderPackage DecoderPackage { get; }
    }

    /// <summary>
    /// Result of the Decode() function. Predominantly an array of SegmenterTokens,
    /// which carry surface form, boundary flags, and alignment info.
    /// </summary>
    public abstract class IDecoded
    {
        /// <summary>
        /// The decoded line as consecutive sub-strings that represent the original tokenization from translation,
        /// but with spaces inserted. The decoded line can be formed by straight concatenation of the tokens' SurfaceForm fields.
        /// </summary>
        public abstract DecodedSegment[] Tokens { get; }
        /// <summary>
        /// The final decoded line as raw plain text. Same as concatenating all SegmenterToken[].SurfaceForm
        /// </summary>
        public override string ToString() => "".JoinItems(from token in Tokens select token.SurfaceForm);
    }

    /// <summary>
    /// Base class that is used to invoke segmenters (SentencePiece or FactoredSegmenter).
    /// A segmenter is an object that can encode / decode a single language's strings (We need the parallel segmenter for runtime and training)
    /// at runtime.
    /// Such a segmenter does (ideally) not know about the translation process (the parallel segmenter does).
    /// </summary>
    public abstract class SegmenterCoderBase
    {
        public abstract IEncoded Encode(string line,
                                        List<AnnotatedSpan> annotatedSpans = null, Dictionary<string, string> sourceSentenceAnnotations = null,
                                        int? seed = null);

        /// <summary>
        /// Decode a line of tokens in Marian-internal string format from in-memory data structures.
        /// Spaces are individual tokens in the output.
        /// </summary>
        public abstract IDecoded Decode(IEnumerable<string> encodedTokensFromMT,
                                        Alignment alignmentFromMT,
                                        IDecoderPackage decoderPackage);

        /// <summary>
        /// Decode a line of tokens in serialized Marian-NMT form, e.g. the result of Marian
        /// translation as written to a file. This overload does not support alignments.
        /// </summary>
        public IDecoded Decode(string line, IDecoderPackage decoderPackage = null)
        {
            return Decode(line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList(), null, decoderPackage);
        }

        /// <summary>
        /// Create a SegmenterCoder from a config. The kind of segmenter is determined
        /// from the config's actual type.
        /// </summary>
        public static SegmenterCoderBase CreateForKindOf(SegmenterCoderConfig config)
        {
            if (config == null)
                return null;
            switch (config.SegmenterKind)
            {
                case SegmenterKind.FactoredSegmenter:
                    // @TODO: what do we put into the SegmenterCoderConfig config? Maybe a FactoredSegmenterCoderConfig?
                    return new FactoredSegmenterCoder(new FactoredSegmenterCoderConfig { ModelPath = config.ModelPath });
                case SegmenterKind.SentencePiece:
                default:
                    throw new NotImplementedException();
            }
        }

        // special functions for shortlist generation, for use by PureNeuralTools/lex_trans_to_shortlist

        /// <summary>
        /// Retrieve the shortlist vocabulary. This is for use by PureNeuralTools/lex_trans_to_shortlist.
        /// </summary>
        public abstract string[] ShortlistVocab { get; }

        /// <summary>
        /// Transcode a token (in segmenter-encoded form) into the shortlist token (in segmenter-encoded form).
        /// </summary>
        public abstract string TranscodeTokenToShortlist(string token);

        /// <summary>
        /// Why we need this flag? We may want to log strings, do additional checks, fail during training code path. However, at runtime -- when running in our cluster
        /// we have strict requirements. This flag is used to indicate the same.  if this is set to false (default = true),we cannot log any user strings at runtime.
        /// </summary>
        public bool IsTrainingScenario { get; set; }
    }

    // Unimplemented version, if we wanted to use raw sentence piece instead of Factored segmenter. We'd need to figure out how to handle tags and other spans.
    public class SentencePieceSegmenterCoder : SegmenterCoderBase
    {
        SentencePieceCoder coder;

        public SentencePieceSegmenterCoder(string modelPath)
        {
            coder = new SentencePieceCoder(new SentencePieceCoderConfig { SentencePieceModel = SentencePieceModel.Load(modelPath) });
        }

        public override IEncoded Encode(string line, List<AnnotatedSpan> annotatedSpans = null, Dictionary<string, string> sourceSentenceAnnotations = null, int? seed = null)
        {
            throw new NotImplementedException();
        }

        public override IDecoded Decode(
            IEnumerable<string> encodedTokensFromMT,
            Alignment alignmentFromMT,
            IDecoderPackage decoderPackage)
        {
            throw new NotImplementedException();
        }

        public override string[] ShortlistVocab { get { throw new NotImplementedException(); } }

        public override string TranscodeTokenToShortlist(string token)
        {
            throw new NotImplementedException();
        }
    }
}
