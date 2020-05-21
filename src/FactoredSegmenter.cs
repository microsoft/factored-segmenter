using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Common.Collections.Extensions;
using Common.Contracts;
using Common.MT.Segments;
using Common.Text;
using Common.Utils;
using Microsoft.MT.Common.Tokenization.Segmenter;
using Microsoft.MT.TextSegmentation.SpanFinder;

/// <summary>
/// This source file is the FactoredSegmenter. The exported classes are:
///  - FactoredSegmenterModel: represents the trained model for segmentations (including the SentencePiece model)
///  - FactoredSegmenterCoder: contains the functions for encoding and decoding; uses FactoredSegmenterModel as one of its inputs
///  - FactoredSegmenterCoderConfig: runtime parameters for FactoredSegmenterCoder
/// To read this source code, skip to these classes.
/// </summary>
namespace Microsoft.MT.Common.Tokenization
{
    /// <summary>
    /// AllFactorTypes, FactorType, and Factor have the purpose of representing
    /// factors and their types as singleton objects that can be compared by comparing
    /// their references by object identity.
    /// Class 'Factors' holds static references to these.
    /// </summary>
    internal class AllFactorTypes
    {
        public Dictionary<string, FactorType> allFactorTypes = new Dictionary<string, FactorType>(); // type string -> type
        public Dictionary<string, FactorType> serializedFormMap = new Dictionary<string, FactorType>(); // serialized factor -> type
        public void Add(string key, FactorType factorType) { allFactorTypes[key] = factorType; }
        public void Add(Factor val) { serializedFormMap[val.Serialized] = val.type; }
        public FactorType Find(string serializedFactor)
        {
            Sanity.Requires(serializedFormMap.TryGetValue(serializedFactor, out FactorType factorType), $"Unknown type for factor {serializedFactor}");
            return factorType;
        }
    }
    internal class FactorType
    {
        public readonly string key;
        AllFactorTypes allFactorTypes;
        Dictionary<string, Factor> serializedFormMap; // serialized form -> Factor
        public FactorType(string key, AllFactorTypes allFactorTypes)
        {
            if (Token.CLASS_SEPARATOR != '@') // new form; @TODO: change factor strings below to lc
                key = key.ToLowerInvariant();
            this.key = key;
            this.serializedFormMap = new Dictionary<string, Factor>();
            this.allFactorTypes = allFactorTypes;
            allFactorTypes.Add(key, this);
        }
        public void Add(Factor val) { serializedFormMap.Add(val.Serialized, val); allFactorTypes.Add(val); }
        public Factor Find(string serializedFactor)
        {
            Sanity.Requires(serializedFormMap.TryGetValue(serializedFactor, out Factor factor), $"Invalid factor {serializedFactor}");
            return factor;
        }
        public IEnumerable<Factor> Factors => serializedFormMap.Values;
        public override string ToString() => key;
    }
    internal class Factor
    {
        public Factor(FactorType type, string val)
        {
            if (Token.CLASS_SEPARATOR != '@') // new form; @TODO: change factor strings below to lc
                val = val.ToLowerInvariant();
            this.type = type;
            this.Serialized = type.key + val;
            type.Add(this);
        }
        public readonly FactorType type;
        public readonly string Serialized;
        public override string ToString() => Serialized;
    }

    /// <summary>
    /// This struct represents the set of factors associated with a Token.
    /// Factors are stored as references to singleton objects, which are compared
    /// by object identity.
    /// This is a value type (struct) and not a reference type (class) because a common
    /// use pattern is to copy and modify a Factors struct.
    /// </summary>
    // @TODO: address this PR comment from Pete in some form:
    // "This seems to be a collection of specific types of factors. Would be nice if the name reflected that better. Factors is pretty general-purpose and uninformative."
    internal struct Factors
    {
        // static singleton objects that are used throughout this module to represent factors and their types
        // @TODO: also include?
        //     - char class
        //     - script (of first char)
        readonly internal static AllFactorTypes allFactorTypes = new AllFactorTypes();
        readonly internal static FactorType CAP_TYPE = new FactorType("C", allFactorTypes);
        readonly internal static FactorType SINGLE_CAP_TYPE = new FactorType("SC", allFactorTypes);
        readonly internal static FactorType GLUE_LEFT_TYPE  = new FactorType("GL", allFactorTypes);
        readonly internal static FactorType GLUE_RIGHT_TYPE = new FactorType("GR", allFactorTypes);
        readonly internal static FactorType WORD_BEG_TYPE = new FactorType("WB", allFactorTypes);
        readonly internal static FactorType WORD_END_TYPE = new FactorType("WE", allFactorTypes);
        readonly internal static FactorType WORD_INT_TYPE = new FactorType("WI", allFactorTypes);
        readonly internal static FactorType CS_BEG_TYPE = new FactorType("CB", allFactorTypes); // continuous-script segment, e.g. Chinese or Thai
        readonly internal static FactorType CS_END_TYPE = new FactorType("CE", allFactorTypes);
        readonly internal static FactorType CLASS_TYPE = new FactorType("class", allFactorTypes);
        readonly internal static FactorType INDEX_TYPE = new FactorType("index", allFactorTypes);
        readonly internal static FactorType INLINE_FIX_TYPE = new FactorType("I", allFactorTypes);
        readonly internal static Factor CAP_INITIAL = new Factor(CAP_TYPE, "I"); // first char is capitalized
        readonly internal static Factor CAP_ALL     = new Factor(CAP_TYPE, "A"); // all chars are capitalized. Note: single cap letters are CAP_INITIAL, not CAP_ALL
        readonly internal static Factor CAP_NONE    = new Factor(CAP_TYPE, "N"); // no char is capitalized
        readonly internal static Factor SINGLE_CAP_UPPER  = new Factor(SINGLE_CAP_TYPE, "U"); // single letter is capitalized (SingleLetterCaseFactors only)
        readonly internal static Factor SINGLE_CAP_LOWER  = new Factor(SINGLE_CAP_TYPE, "L"); // single letter is not capitalized (SingleLetterCaseFactors only)
        readonly internal static Factor GLUE_LEFT      = new Factor(GLUE_LEFT_TYPE, "+"); // glue left means no space to the left
        readonly internal static Factor GLUE_LEFT_NOT  = new Factor(GLUE_LEFT_TYPE, "-"); // Glues are only used for punctuation, not for words.
        readonly internal static Factor GLUE_RIGHT     = new Factor(GLUE_RIGHT_TYPE, "+");
        readonly internal static Factor GLUE_RIGHT_NOT = new Factor(GLUE_RIGHT_TYPE, "-");
        readonly internal static Factor WORD_BEG     = new Factor(WORD_BEG_TYPE, ""); // beginning of a word in a spaced script, e.g. used in English
        readonly internal static Factor WORD_BEG_NOT = new Factor(WORD_BEG_TYPE, "N"); // (note: _NOT variant not used if DistinguishInitialAndInternalPieces)
        readonly internal static Factor WORD_END     = new Factor(WORD_END_TYPE, "");
        readonly internal static Factor WORD_END_NOT = new Factor(WORD_END_TYPE, "N");
        readonly internal static Factor WORD_INT     = new Factor(WORD_INT_TYPE, ""); // is word-internal (DistinguishInitialAndInternalPieces only)
        readonly internal static Factor CS_BEG     = new Factor(CS_BEG_TYPE, "");  // continuous-script (e.g. CJK or Thai): first item in consecutive range of CS characters
        readonly internal static Factor CS_BEG_NOT = new Factor(CS_BEG_TYPE, "N"); // internal split point within a CS segment
        readonly internal static Factor CS_END     = new Factor(CS_END_TYPE, "");  // This is distinguished from WORD_BEG because the surrounding-space rules are different.
        readonly internal static Factor CS_END_NOT = new Factor(CS_END_TYPE, "N");
        readonly internal static Factor INLINE_FIX_NONE = new Factor(INLINE_FIX_TYPE, "N");
        readonly internal static Factor INLINE_FIX_WHAT = new Factor(INLINE_FIX_TYPE, "S"); // replace a token sequence with this factor...
        readonly internal static Factor INLINE_FIX_WITH = new Factor(INLINE_FIX_TYPE, "T"); // ...with this one, which immediately follows
        // @BUGBUG: For now restrict to PhraseFix, to keep total word-id space smaller.
        readonly internal static Dictionary<AnnotatedSpanClassType, Factor> CLASS_FACTORS = // factors for word classes 'class' + AnnotatedSpanClassType.ToString(), e.g. classPhraseFix
            (from classType in new[] { AnnotatedSpanClassType.PhraseFix }//Enum.GetValues(typeof(AnnotatedSpanClassType)).Cast<AnnotatedSpanClassType>()
             select (classType, new Factor(CLASS_TYPE, classType.ToString()))).ToDictionary(kvp => kvp.Item1, kvp => kvp.Item2);
        readonly internal static Factor[] INDEX_FACTORS = // factors for indices of word classes, e.g. classPhraseFix|index013
            (from i in Enumerable.Range(0, 40) // @BUGBUG: can't go higher without changing to 64-bit ints in Marian. Alternative implementation ongoing.
             select new Factor(INDEX_TYPE, i.ToString("D3"))).ToArray();

        // the factors
        public Factor cap;       // casing of a word (all lower, all caps, cap-initial); or null for tokens that are not made of bicameral letters
        public Factor singleCap; // casing factor for a single letters (only if SingleLetterCaseFactors)
        public Factor glueLeft;  // should token glue to left neighbor without space? Used internally for all tokens; but at the end for punctuation only
        public Factor glueRight; // likewise, glue to right?
        public Factor wordBeg;   // is this token the first or not the first of a word? Null if not part of a word.
        public Factor wordEnd;   // likewise, last of a word?
        public Factor wordInt;   // is it word-internal? (used instead of WORD_BEG_NOT if DistinguishInitialAndInternalPieces)
        public Factor csBeg;     // is this token the first or not the first of a stretch of continuous-script characters (e.g. Chinese)? Null if not continuous script
        public Factor csEnd;     // likewise, last of stretch?
        public Factor spanClass; // class of the token if any. If set, Word must be ""
        public Factor index;     // index of class token. Every spanClass has an index.
        public Factor inlineFix; // mark-up for source-target pairs embedded in source string, e.g. "NAME|nn JOHN|ns SMITH|ns JOHANN|nt SCHMIDT|nt"

        public Factors(Factors other)
        {
            cap = other.cap;
            singleCap = other.singleCap;
            glueLeft  = other.glueLeft;
            glueRight = other.glueRight;
            wordBeg = other.wordBeg;
            wordEnd = other.wordEnd;
            wordInt = other.wordInt;
            csBeg = other.csBeg;
            csEnd = other.csEnd;
            spanClass = other.spanClass;
            index = other.index;
            inlineFix = other.inlineFix;
        }

        private IEnumerable<Factor> AllFactors
        {
            get
            {
                yield return cap;
                yield return singleCap;
                yield return glueLeft;
                yield return glueRight;
                yield return wordBeg;
                yield return wordEnd;
                yield return wordInt;
                yield return csBeg;
                yield return csEnd;
                yield return spanClass;
                yield return index;
                yield return inlineFix;
            }
        }

        public IEnumerable<Factor> Values => from factor in AllFactors where factor != null select factor; // used to Serialize()
        public IEnumerable<FactorType> Types => from value in Values select value.type; // used in creating the vocab

        // construct a Factorsobject from its string representation
        public static Factors Deserialize(IEnumerable<string> factorStrings)
        {
            var res = new Factors();
            foreach (var factorString in factorStrings)
            {
                var type = allFactorTypes.Find(factorString);
                var factor = type.Find(factorString);
                if      (type == CAP_TYPE)        res.cap       = factor;
                else if (type == SINGLE_CAP_TYPE) res.singleCap = factor;
                else if (type == GLUE_LEFT_TYPE)  res.glueLeft  = factor;
                else if (type == GLUE_RIGHT_TYPE) res.glueRight = factor;
                else if (type == WORD_BEG_TYPE)   res.wordBeg   = factor;
                else if (type == WORD_END_TYPE)   res.wordEnd   = factor;
                else if (type == WORD_INT_TYPE)   res.wordInt   = factor;
                else if (type == CS_BEG_TYPE)     res.csBeg     = factor;
                else if (type == CS_END_TYPE)     res.csEnd     = factor;
                else if (type == CLASS_TYPE)      res.spanClass = factor;
                else if (type == INDEX_TYPE)      res.index     = factor;
                else if (type == INLINE_FIX_TYPE) res.inlineFix = factor;
                else throw new ArgumentException($"Invalid factor {factorString}"); // (should not get here)
            }
            return res;
        }

        public override string ToString() => Token.CLASS_SEPARATOR.ToString().JoinItems(from factor in AllFactors select factor.Serialized);
    }

    /// <summary>
    /// Token represents a factored token as used by Marian.
    /// Tokens consist of a string that is stored as a character-range reference to an underlying string,
    /// as well as a set of factors.
    /// </summary>
    internal struct Token
    {
        /*const*/
        //internal static readonly char CLASS_SEPARATOR = '@';
        internal static readonly char CLASS_SEPARATOR = '|';
        internal const string WORD_BEG_PREFIX = "\u2581"; // "LOWER ONE EIGHTH BLOCK" prefix for word beginning; same underscore-like symbol as SentencePiece (DistinguishInitialAndInternalPieces mode only)
        internal const string CLASS_LEMMA_WORD         = "{word}";             // word classes are represented by these lemmas, which differ in their factors
        internal const string CLASS_LEMMA_WORD_NO_CASE = "{word-wo-case}";     // word in non-casing script, e.g. Hindi
        internal const string CLASS_LEMMA_CS           = "{continuousScript}"; // word in non-spacing script, e.g. Chinese
        internal const string CLASS_LEMMA_PUNC         = "{punctuation}";      // punctuation
        internal static readonly Dictionary<string,string> ALL_CLASS_LEMMAS = new Dictionary<string,string> // [class] -> example string
        {
            { CLASS_LEMMA_WORD, "Hello" },         // note: The example strings are used to derive the factor types for the respective class.
            { CLASS_LEMMA_WORD_NO_CASE, "नमस्ते" },  //       See GetFactorsFromExample() for more explanation.
            { CLASS_LEMMA_CS,   "你好" },
            { CLASS_LEMMA_PUNC, "!" }
        };

        // test whether we must not lower-case this since it is a class token (or punctuation with {} that for sure has no capitals either)
        // Only used in DeserializeLemma(). Hopefully we can get rid of this by not lower-casing internally at all.
        internal static bool IsClassLemmaOrSomePunc(string s) => s.Length > 2 && s[0] == '{' && s[s.Length - 1] == '}';
        //  - @TODO: do we need CLASS_LEMMA_LETTER, for single-letter factor?

        // @TODO: The following is ongoing, unfinished work:
        // how index and/or unrepresentables are represented:
        //  - phrase fixes:
        //     - legacy:  {word}|classPhraseFix|index042|ci|wb
        //     - {word}|classPhraseFix|ci|wb <4> <2> <#>  --encodes class 'word' with index '42'
        //  - unencodables:
        //     - {unk,c,wb}|cn|wb <3> <5> <3> <#>     --encodes unrepresentable Unicode character &#353;
        //  - notes:
        //     - digit sequence is encoded the same way for both phrase fix and unencodable, so model can tie the related parameters
        //     - we cannot use the phrase-fix mechanism for unrepresentables because at training
        //       time, it is not guaranteed that there is a 1:1 match between source and target
        //     - unrepresentables nevertheless have regular caps and spacing factors, depending on their word/punc nature.
        //       Since each lemma has a fixed set of factors, lemma names encode the factor set.
        //  - flow:
        //     - encode:
        //        - keep unrepresentable tokens as-is until late stage (with full factors)
        //        - at end, add a transform to convert the token sequence to {unk...} <#> form
        //           - replace unrepr. char lemma (which must be a single char) by {unk,factors}
        //           - insert plain tokens (no factors) for the digit string and the <#>
        //     - decode:
        //        - at early stage, transform such token sequences to regular tokens (containing the unrepresentable char)

        internal const string UNK_LEMMA_PREFIX = "{unk";
        internal const string UNK_LEMMA_PATTERN = UNK_LEMMA_PREFIX + "*}"; // '*' gets replaced by comma-separated factor list
        internal static readonly string[] UNK_DIGITS = { "<0>", "<1>", "<2>", "<3>", "<4>", "<5>", "<6>", "<7>", "<8>", "<9>" };
        internal const string UNK_DIGITS_END = "<#>";
        internal const string SLA_LEMMA_PREFIX = "<sla:"; // sentence-level annotations are passed to Marian as tokens of the form <sla:type=value>

        // The original string is distinguished from the "underlying" string so that
        // we can keep track of the original character range that a token originated from.
        // The original string is the raw line that the text originated from.
        // The underlying string may be either identical to the original string,
        // or a substitute. If it is a a substitute, the Narrow() function will not
        // modify the original character range; for substitutes, we just track the
        // original character range, and cannot further sub-divide it (any potential
        // subwords of this string are considered originating from the same full original character range).
        private string origLine;                // original string
        private int origStartIndex, origLength; // character range into original string
        private string line;                    // underlying string
        private int startIndex, length;         // character range into underlying string
        public Factors factors;                 // [first char of factor name] -> factor name

        // create a new Token from an entire line
        public Token(string line, Factors factors = new Factors(), bool lemmaWordBegPrefix = false)
        {
            this.origLine = line;
            this.origStartIndex = 0;
            this.origLength = line.Length;
            this.line = line;
            this.startIndex = 0;
            this.length = line.Length;
            this.factors = factors;
        }

        // create a new Token by replacing the underlying string
        // This token retains the original line and character range into it.
        public Token OverrideAsIf(string newLine)
        {
            var token = this;
            token.line = newLine;
            token.startIndex = 0;
            token.length = newLine.Length;
            //Sanity.Requires(!lemmaWordBegPrefix, "lemmaWordBegPrefix should not have been set at this point"); // now it can, as we use this function in serialization of indices and unrepresentables
            return token;
        }

        // create a new Token with an zero-length reference to the original string of 'this'
        // This is used by serializing index factors and unrepresentables, for the digit tokens.
        public Token PseudoTokenAt(string s, bool after)
        {
            var token = this;
            if (after)
                token.origStartIndex += token.origLength;
            token.origLength = 0;          // collapse original reference, point to end of token
            token.factors = new Factors(); // nuke all factors
            return token.OverrideAsIf(s);  // and implant a new string
        }

        // create a new token that is a narrowed version of the original
        // E.g. the Token("abcde").Narrow(1,2) will give a new token "bc".
        // If the Token still refers to its original string, then the original range
        // is narrowed as well. If not, then that's it, any further replacement will
        // be considered aligned with the entire original range.
        public Token Narrow(int newStartIndex, int newLength)
        {
            var token = this;
            token.startIndex += newStartIndex;
            token.length = newLength;
            if (OrigLineIs(token.line)) // still referring to the original object? (cf. function-level comment)
            {
                token.origStartIndex += newStartIndex;
                token.origLength = newLength;
            }
            return token;
        }

        // helper to verify that the original line. Non-private only for a sanity check.
        internal bool OrigLineIs(string line) => object.ReferenceEquals(origLine, line);

        // properties
        public string Word => line.Substring(startIndex, length);
        public int Length => length; // note: this is UCS-2 length, which differs from #chars in presence of surrogate pairs
        public (int StartIndex, int Length) OrigRange => (origStartIndex, origLength); // originating character range
        public string OrigString => origLine.Substring(origStartIndex, origLength);
        public char First() => line[startIndex]; // note: caller must ensure length > 0
        public char At(int i) => line[startIndex + i]; // note: caller must ensure i < length

        // conversions to output representations
        public string SurfaceForm(Dictionary<int, string> decodeAsTable = null) // convert token into the display form (used in Decode()), i.e. apply capitalization factor
        {
            var word = Word;
            if (factors.index != null) // index factor means map back; and Word is empty
            {
                Sanity.Requires(word == "", "Token with index factor was not deserialized as empty string??");
                int index = ParseIndexFactor();
                if (decodeAsTable != null && decodeAsTable.TryGetValue(index, out var decodeAs))
                    word = decodeAs; // note: leave as empty string for spurious indices that were not in the source

                // If we have a DecodeAs token (factors.index == null) that has contains capitalized characters, 
                // we don't want to force them to lowercase as we do below for normal tokens. If the DecodeAs
                // is not already uppercase, however, we may still want to capitalize it if specified by factors,
                // for example at the beginning of a sentence.
            }

            // Normal word, should rely only on factors to determine capitalization - 
            // lowercase everything first and then change to upper where specified by factors.
            else
            {
                word = word.ToLowerInvariant();
            }

            if (factors.cap == Factors.CAP_ALL || factors.singleCap == Factors.SINGLE_CAP_UPPER)
                return word.ToUpperInvariant();
            else if (factors.cap == Factors.CAP_INITIAL)
                return word.Capitalized();
            else
                return word;
        }

        // helper to parse the index factor into an integer ("index042" -> 42)
        internal int ParseIndexFactor()
        {
            var indexStr = factors.index.ToString().Substring(factors.index.type.ToString().Length); // split off factor name; rest is the number
            return int.Parse(indexStr);
        }

        // internal normalized representation of lemma factor, as used by SPM
        // If we distinguish initial and internal word pieces, we also prefix it with SPM's _ character.
        // The class serialization form ("{word}") is *not* handled here.
        public string SubStringNormalizedForSPM(bool lemmaWordBegPrefix)
        {
            var word = Word;
            if (CLASS_SEPARATOR == '@') // legacy form
            {
                if (word[0].HasAndIsUpper())    // lower-case it if needed --@BUGBUG: this short-circuit fails in presence of combiners
                    word = word.ToLowerInvariant();
            }
            else if (IsOfWordNature)
                word = Word.ToUpperInvariant();
            else
                word = Word;
            if (lemmaWordBegPrefix)
                word = WORD_BEG_PREFIX + word;
            return word;
        }

        // internal normalized representation of lemma factor; we use upper-case for readability
        // The class serialization form ("{word}") *is* handled here.
        public string SubStringNormalizedForLemma(FactoredSegmenterModelOptions modelOptions)
        {
            string word;
            if (IsClass)
                word = CLASS_LEMMA_WORD; // @TODO or {continuousScript} or {punctuation}
            else if (IsSpecialToken)
                word = Word;
            else if (IsOfWordNature)
                word = Word.ToUpperInvariant();
            else
                word = Word;
            if (modelOptions.DistinguishInitialAndInternalPieces && factors.wordBeg == Factors.WORD_BEG)
                // with this flag set, the WORD_BEG factor also implies a _ on the lemma
                word = WORD_BEG_PREFIX + word;
            return word;
        }

        // Tokens are of one of the two natures of "Word" (carries word-boundary info) or of
        // "Punc" (carries spacing info). (Outside of this, there is also the "special token", cf. below).
        // The main criterion for Word-ness is whether the token consists of letters/numerals.
        // All characters in a token must be of the major designation, due to our pre-tokenization
        // by that criterion, so we only need to check the first character.
        // Special cases:
        //  - Class tokens indicate phrase fixes and are therefore also considered of Word nature.
        //  - Empty string can be either a class token (phrase-fix), a character range declared by
        //    the user as EncodeAsIf("") (to force a break), or SentencePiece breaking after
        //    the _ prefix in DistinguishInitialAndInternalPieces mode. The latter requires
        //    it to be of Word nature.
        //  - Tokens may begin with a combiner, as a result of SentencePiece.
        //    We assume that each combiner is applied to only one character type. This assumption
        //    is already used in pre-tokenization. This way, we get uniform Word-ness for all
        //    characters of a word, even if further broken up by SentencePiece.
        //    (In reality, combiners inherit from the left context, but that can cause the same
        //    single-combiner token get different sets of factors, which is forbidden. We hope
        //    that our assumption is correct most of the time, and that the NMT system can learn
        //    the cases where it is not as special cases.)
        //    @BUGBUG: We don't handle combiners with continuous scripts. Those can still lead
        //    to inconsistent pre-tokenization, where a CS sequence (with a combiner) can be
        //    split by SentencePiece and the combiner ends up being as non-CS Word.
        public bool IsOfWordNature => Sanity.Requires(!IsSpecialTokenWithoutFactors, "IsOfWordNature called on special token") && (
                                       IsClass ||
                                       Length == 0 ||
                                       First().IsLetterOrLetterCombiner() ||
                                       First().IsNumeral()); // numerals count as words
        public bool IsOfPuncNature => Sanity.Requires(!IsSpecialTokenWithoutFactors, "IsOfPuncNature called on special token") && !IsOfWordNature;

        // Special tokens are tokens used internally by FactoredSegmenter and/or Marian.
        //  - Special tokens without factors borrow the form of simple XML tags, e.g. </s> (Marian) or <4> (index serialization).
        //  - Special tokens with factors have the form {type,factor-types}, e.g. {unk,c,wb}, or fixed forms, e.g. {word}.
        // It is guaranteed that such tokens are never generated from external input,
        // by enforcing that < > { } in input are always split into single characters.
        // Most special tokens do not occur during most of the steps inside of Encode(),
        // and are only generated towards the end (e.g. {unk,...}), inside Marian (</s> <unk>),
        // or directly encoded in the lemma only (e.g. {word}).
        public bool IsSpecialTokenWithoutFactors => length > 2 && IsSpecialTokenWithoutFactorsDelimiter(First());
        public bool IsSpecialToken => length > 2 && IsSpecialTokenDelimiter(First());
        public static bool IsSpecialTokenLemma(string lemma) => lemma.Length > 2 && IsSpecialTokenDelimiter(lemma[0]);
        public static bool IsSpecialTokenDelimiter(char c) => IsSpecialTokenWithoutFactorsDelimiter(c) || c == '{';
        public static bool IsSpecialTokenWithoutFactorsDelimiter(char c) => c == '<';

        public bool IsClass => factors.spanClass != null;
        public bool IsContinuousScript =>
            Sanity.Requires(IsOfWordNature, "Tested non-Word for IsContinuousScript??") &&
            !IsClass && Length > 0 && First().IsContinuousScript(); // only valid if IsOfWordNature  --@TODO: we can remove the !IsClass
        public bool IsSpace => Length == 1 && First() == ' '; // optimized version of Word == " " avoiding SubString()

        public bool IsBicameral() // only valid if IsOfWordNature. Does the token contain at least one bicameral letter (==does the word use a script that uses lower and upper case letters)
        {
            Sanity.Requires(IsOfWordNature, "Tested non-Word for IsBicameral??");
            // note: after pre-tokenization, all chars in the Token have the same script (combiners inherit their character's script)
            // note: this will correctly classify tokens that contain combiners (combiner-only tokens are not considered bicameral)
            for (var i = 0; i < length; i++)
                if (line[startIndex + i].IsBicameral())
                    return true;
            return false;
        }
        public bool NeedsCapsFactor() // test if token should have caps factor
        {
            return IsOfWordNature &&
                   (IsBicameral() ||
                    (Length > 0 && First() == 'ß'));  // 'ß' should get caps factor for consistency  --@TODO: think this through
        }
        public bool IsAllCombiners() // does token consist entirely of combiners? (at least single-combiner tokens are not unusual)
        {
            for (var i = 0; i < length; i++)
                if (!line[startIndex + i].IsCombiner())
                    return false;
            return length > 0;
        }
        public bool HasAndIsAllCaps() // tests if token is all-caps (at least one char has lower and upper form, and all of those are upper)
        {
            // we must special-case combiners (which are skipped) and German ess-zet (which has no uppercase form)
            int combinersSkipped = 0;
            for (var i = 0; i < length; i++)
                if (!line.HasAndIsUpperAt(startIndex + i)) // found a lower-case or non-bicameral char
                    if (line[startIndex + i].IsCombiner()) // ignore combiners
                    {
                        combinersSkipped++;
                        continue;
                    }
                    else if (length == 1 || line[startIndex + i] != 'ß') // ess-zet has no capitalized version. A single ess-zet surrounded by caps still counts as all-caps  --@TODO: the condition is not strict
                        return false;
            return length > combinersSkipped; // all chars (except combiners) are capitalized (=differ from their ToLowerInvariant form)
        }

        // helper to split a token according to a cutList
        // All factors are duplicated to the pieces, except for the glue factors, which are set to reflect the split.
        internal IEnumerable<Token> SplitToken(IList<int> cutList) // e.g. "abcde", (0,2,5) -> {"ab", "cde"}
        {
            // cutList is relative to the token
            var token = this; // (need to copy so that it can be used inside the LINQ expression)
            if (cutList == null)
                return new Token[] { token }; // if no cut-list then return unmodified (SPM did not split)
            else
            {
                var res = new List<Token>();
                for (int i = 0; i + 1 < cutList.Count; i++) // (cutList includes 0 and token.s.Length)
                    res.Add(token.Narrow(cutList[i], cutList[i + 1] - cutList[i]));
                return res;
            }
        }

        // helper to split a token by a predicate function, and append the result directly to a List
        internal void SplitAndAddTo(Func<string, int, bool> ShouldSplitFunc, List<Token> res)
        {
            if (length == 0) // empty token is not split. E.g. this is a class token, or an actual empty string.
            {
                res.Add(this);
                return;
            }
            var token = this;
            var i0 = 0;
            for (var i = 1; i <= token.length; i++)
            {
                if (i == token.length || ShouldSplitFunc(token.line, token.startIndex + i))
                {
                    if (i0 == 0 && i == token.length)
                        res.Add(token);
                    else
                        res.Add(token.Narrow(i0, i - i0));
                    i0 = i;
                }
            }
        }

        static bool NeedsEscaping(char c) // check whether char is one of the special characters that should be escaped in the serialized form
        {
            //       - space, non-printing, glue names < and > , \ , and our factor separator |
#if True    // legacy with running experiments; remove this soon
            if (CLASS_SEPARATOR == '@')
                return
                    c.IsInRange(0, 32) || c == '<' || c == '>' || c == '\\' || c == CLASS_SEPARATOR ||
                    c == '\uFFFF' || // Invalid char showed up in data
                    c.IsInRange(0xD800, 0xDFFF);
#endif
            return
                c.IsInRange(0, 32) ||                                       // non-printable
                c == '\\' ||                                                // the escape prefix itself
                c == CLASS_SEPARATOR ||                                     // needed in serializing data
                c == '_' || c == ':' || c == '#' ||                         // used by vocab-file syntax
                c == '<' || c == '>' ||                                     // used by vocab-file syntax and special symbols
                c == '\uFFFF' ||                                            // invalid char showed up in data
                c.IsInRange(0xD800, 0xDFFF); // I hit an isolated high surrogate, which caused an error in writing
            // @BUGBUG: We also need to escape the word string "NULL", which has special meaning in some shortlist-related tools
        }
        internal static bool NeedsEscaping(string s) => s.Any(c => NeedsEscaping(c)); // (for writing the vocab)

        internal static StringBuilder SerializeLemma(string s, int extraCapacity = 0) // escape unprintable chars to \x.. or \u.... notation
        {
            // This returns the StringBuilder because we need the result in a StringBuilder anyways.
            StringBuilder sb = null;
            if (!Token.IsSpecialTokenLemma(s))
                for (var i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (NeedsEscaping(c))
                    {
                        if (sb == null) // lazily create the StringBuilder only when necessary
                            sb = new StringBuilder(s.Substring(0, i), i + (s.Length - i) * 6 + extraCapacity); // 6=max length of one escaped character
                        bool isX = (c < (char)256);
                        sb.Append(isX ? "\\x" : "\\u");
                        sb.Append(((short)c).ToString(isX ? "X2" : "X4"));
                    }
                    else
                        if (sb != null)
                        sb.Append(c);
                }
            if (sb == null)
                return new StringBuilder(s, extraCapacity);
            else
                return sb;
        }

        internal static string DeserializeLemma(string s) // un-escape \x and \u expressions generated by SerializeTokenChar
        {
            // @TODO: This is highly inefficient (but only used for checks and decoding, so it's OK for now)
            var pieces = s.Split(new char[] { '\\' }, StringSplitOptions.None);
            return pieces[0] + "".JoinItems(
                from piece in pieces.Skip(1)
                let xu = piece.First()
                let numDigits = (xu == 'x' ? 2 : xu == 'u' ? 4 : throw new FormatException("Backslash expressions can only be \xDD or \uDDDD"))
                let charCode = int.Parse(piece.Substring(1, numDigits), System.Globalization.NumberStyles.HexNumber)
                select ((char)charCode).ToString() + piece.Substring(numDigits + 1));
        }

        // serialize this Token to a string (that is, to lemma/factor syntax)
        // When 'withoutFactors' is true, then this generates the lemma only. Used for shortlists.
        internal string Serialize(FactoredSegmenterModelOptions modelOptions, bool withoutFactors = false)
        {
            // - serialize to line encoding
            //    - escape lemmas
            //       - space, non-printing, glue names < and > , \ , and our factor separator |
            //       - into \xXX codes, i.e. [\x00-\x20\x3c\x3e\x5c\x7c]
            //    - concatenate all factors with | inbetween
            //    - concatenate all tokens with a space inbetween
            var lemma = SubStringNormalizedForLemma(modelOptions);
            var sb = SerializeLemma(lemma, extraCapacity: 30);
            if (!withoutFactors)
                foreach (var factor in factors.Values)
                {
                    sb.Append(CLASS_SEPARATOR);
                    sb.Append(factor.Serialized);
                }
            return sb.ToString();
        }

        public override string ToString() => Serialize(new FactoredSegmenterModelOptions());

        // deserialize a string to a Token struct
        internal static Token Deserialize(string s, FactoredSegmenterModelOptions modelOptions)
        {
            var pieces = s.Split(CLASS_SEPARATOR);
            Sanity.Requires(pieces[0].Length > 0, $"Unexpected lemma of zero length in {s}");
            var lemma = DeserializeLemma(pieces[0]);
            if (modelOptions.DistinguishInitialAndInternalPieces && lemma[0] == WORD_BEG_PREFIX[0])
                lemma = lemma.Substring(1); // note: empty lemma is allowed here, because WORD_BEG_PREFIX may be its own piece
            var factors = Factors.Deserialize(pieces.Skip(1));
            if (factors.index != null) // if index factor given, then lemma is merely external, but internally just empty
            {
#if True        // bug fix: some old models lacked the glue factors in {punctuation}
                if (lemma == "{punctuation}" && factors.glueLeft == null && factors.glueRight == null) // (intentionally using string literal instead of variable)
                {
                    factors.glueLeft  = Factors.GLUE_LEFT;
                    factors.glueRight = Factors.GLUE_RIGHT_NOT;
                }
#endif
                Sanity.Requires(ALL_CLASS_LEMMAS.ContainsKey(lemma), $"Unexpected lemma {lemma} for indexed word class");
                lemma = "";
            }
            var token = new Token(lemma, factors);
            //token.Validate(modelOptions); // currently, we can only Validate() after serialized indices have been converted to a real factor. @TODO: allow to Validate() at this point
            return token;
        }

        internal void Validate(FactoredSegmenterModelOptions modelOptions) // a few checks to catch invalid Tokens
        {
            if (IsSpecialTokenWithoutFactors) // special tokens have no factors
                return;
            Sanity.Requires(factors.spanClass == null || factors.index != null, "Class token unexpectedly has no index factor");
            Sanity.Requires(!NeedsCapsFactor() || factors.cap != null || factors.singleCap != null, "Token lacks capitalization factor");
            Sanity.Requires(factors.glueLeft != null || factors.wordBeg != null || factors.wordInt != null || factors.csBeg != null, "Token lacks left glue/boundary factor");
            Sanity.Requires(factors.glueLeft == null || factors.glueRight != null, "Token lacks right glue/boundary factor");
            Sanity.Requires(Length == 0 || !IsClass, "Token string must be empty if spanClass factor is present");
            Sanity.Requires(Length > 0 || (modelOptions.DistinguishInitialAndInternalPieces && factors.wordBeg != null) || IsClass, "Empty lemma is not allowed unless a spanClass factor is present or it's a lone word-beginning marker");
        }
    }

    /// <summary>
    /// FactoredSegmenter model.
    /// This contains all training-time options as well as the trained SPM model and its factor vocabulary.
    /// </summary>
    public class FactoredSegmenterModel
    {
        static public string ModelFileExtension = "fsm";

        // note: all properties here must have public setters for XML deserialization
        public FactoredSegmenterModelOptions ModelOptions { get; set; } // options we persist

        public byte[] SentencePieceModel { get; set; } // SPM .model file as a binary blob

        /// <summary>
        /// The factor spec is the content of the .fsv file, which is what Marian loads.
        /// No other code should ever look at this file.
        /// </summary>
        public string[] FactorSpec { get; set; }

        ///// <summary>
        ///// The lemma vocabulary is identical to the lemma section in FactorSpec, but as a set.
        ///// It is used to detect unencodable characters.
        ///// Note that these strings are *not* in serialized (escaped) form (unlike ShortlistVocab below).
        ///// </summary>
        //public HashSet<string> KnownLemmas { get; set; }

        /// <summary>
        /// The shortlist vocabulary is used by PureNeuralTools to compute the shortlists and to refer to
        /// them via integer indices that are passed into Marian.
        /// The actual implementation (hidden from outside) is that shortlist tokens are the lemmas.
        /// The shortlist vocab corresponds 100% to the lemma section of the factor vocabulary
        /// in serialized (escaped) form, in the same order.
        /// The factor vocabulary consists of all factor string forms concatenated (each factor's
        /// items are consecutive). The integer indices index this ShortlistVocab.
        /// </summary>
        public string[] ShortlistVocab { get; set; }

        /// <summary>
        /// The lemma vocab is used to detect unrepresentable tokens.
        /// Corresponds 100% to the lemma section of the factor vocabulary.
        /// </summary>
        public HashSet<string> KnownLemmas { get; set; }

        /// <summary>
        /// For systems with sentence-level annotations, such as a language tag,
        /// this property allows to retrieve the full set of annotation types
        /// (as specified in config) and actual values (as observed in training).
        /// </summary>
        [XmlIgnore]
        public Dictionary<string, HashSet<string>> KnownSentenceLevelAnnotations
            => FactoredSegmenterCoder.CollectSentenceAnnotations(this,
                from lemma in KnownLemmas
                let res = FactoredSegmenterCoder.TryToSentenceAnnotationFromString(lemma)
                where res.Found
                select (res.Type, res.Value));

        public FactoredSegmenterModel() : this(null) { } // public parameter-less constructor needed for XML deserialization

        /// <summary>
        /// Serialize model to file
        /// </summary>
        public void Save(string path)
        {
            using (var outputStream = new StreamWriter(path))
                new XmlSerializer(typeof(FactoredSegmenterModel)).Serialize(outputStream, this);
        }

        /// <summary>
        /// Construct this object from file
        /// </summary>
        public static FactoredSegmenterModel Load(string path)
        {
            using (var myFileStream = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                XmlReaderSettings readerSettings = new XmlReaderSettings();
                readerSettings.IgnoreWhitespace = false;
                var model = (FactoredSegmenterModel)new XmlSerializer(typeof(FactoredSegmenterModel)).Deserialize(XmlReader.Create(myFileStream, readerSettings));

                // legacy: SPM was in a separate file
                if (model.SentencePieceModel == null && (bool)model.ModelOptions.UseSentencePiece) // legacy
                {
                    Sanity.Requires(path.EndsWith($".{ModelFileExtension}"), $"Model filename should end in .{ModelFileExtension}: {path}");
                    var spmModelPath = path.Substring(0, path.Length - ModelFileExtension.Length) + "model";
                    model.SentencePieceModel = File.ReadAllBytes(spmModelPath);
                }

                // legacy: even older versions do not contain ShortlistVocab
                if (model.ShortlistVocab == null)
                    model.ShortlistVocab = FactoredSegmenterCoder.LegacyRecoverLemmaVocabFromFactorSpec(model.FactorSpec);

#if True        // a version of the code wrote an incorrectly serialized lemma vocab
                // This code detects such version, and fixes it on the fly.
                // In a few months, this should be changed to a Sanity.Requires(), and few months later,
                // when no more buggy model files are in use, we shall remove this. Bug #100736
                var shortlistVocab2 = FactoredSegmenterCoder.LegacyRecoverLemmaVocabFromFactorSpec(model.FactorSpec);
                if (!shortlistVocab2.SequenceEqual(model.ShortlistVocab))
                {
                    model.ShortlistVocab = shortlistVocab2;
                    Logger.WriteLine("WARNING: Incorrectly serialized shortlist vocab detected; reconstructing the correct one");
                }
#endif

                // legacy: older versions do not contain KnownLemmas
                if (model.KnownLemmas == null || model.KnownLemmas.Count == 0) // (somehow a non-existing XML entry leads to an empty set, not a null??)
                    model.KnownLemmas = new HashSet<string>(
                        (from lemma in (model.ShortlistVocab ?? FactoredSegmenterCoder.LegacyRecoverLemmaVocabFromFactorSpec(model.FactorSpec))
                         select Token.DeserializeLemma(lemma)));
                                        

                return model;
            }
        }

        /// <summary>
        /// Construct this object by training it from data.
        /// 'fsmModelPath' is used to derive a location to temporarily save the generated SPM
        /// model and vocab at (by changing extension .fsm to .spmtmp.model/.spmtmp.vocab). The actual SPM
        /// model is then, however, stored as a binary blob in this class, and the .spmtmp.model file
        /// on disk is no longer used. We do not use a proper temp file for now, as it is
        /// useful for debugging and diagnostics to keep the SPM output files around.
        /// </summary>
        public static FactoredSegmenterModel Train(FactoredSegmenterModelTrainConfig config,
                                                   IEnumerable<string> input, IEnumerable<Dictionary<string,string>> sourceSentenceAnnotations,
                                                   string fsmModelPath, string spmBinDir)
        {
            return FactoredSegmenterCoder.Train(config, input, sourceSentenceAnnotations, fsmModelPath, spmBinDir);
        }

        internal FactoredSegmenterModel(FactoredSegmenterModelOptions modelOptions)
        {
            ModelOptions = modelOptions;
        }
    }

    /// <summary>
    /// Parameters to constructors of FactoredSegmenterCoder.
    /// </summary>
    public class FactoredSegmenterCoderConfig  // @TODO: derive from SegmenterCoderConfig
    {
        /// <summary>
        /// pre-instantiated Model
        /// </summary>
        public FactoredSegmenterModel Model { get; set; }

        /// <summary>
        /// alternative to Model: load Model from this path
        /// </summary>
        public string ModelPath { get; set; }

        // additional options go here
        public int CheckEvery { get; set; } = 100;
    }

    /// <summary>
    /// Main FactoredSegmenter implementation.
    /// Instantiate this for access to Encode() and Decode().
    /// 
    /// This operates on a single side's string. Compare to ParallelSegmenterCoder, which can have source and target models
    /// those are both necessary even at runtime, since we are encoding source and decoding target.
    /// </summary>
    public class FactoredSegmenterCoder : SegmenterCoderBase
    {
        // underlying segmenter model
        FactoredSegmenterModel model;
        internal FactoredSegmenterModel ModelForTestingOnly => model; // internal access for tests only

        // other members
        SentencePieceCoder spmCoder; // underlying instantiated SentencePiece coder instance (or null if none)
        int checkCounter = 0; // (diagnostics only: we run additional consistency checks every 'checkCounter'-th invocation of Encode())
        int checkEvery = 100; // (verify every N-th encoded sentence that when decoding, the original is fully recovered)

        // tag representation for inline fixes in tag mode (format <IOPEN> source string <IDELIM> desired tgt string <ICLOSE>)
        internal const string INLINE_FIX_SRC_ID_TAG = "<IOPEN>";
        internal const string INLINE_FIX_TGT_ID_TAG = "<IDELIM>";
        internal const string INLINE_FIX_END_ID_TAG = "<ICLOSE>";

        // -------------------------------------------------------------------
        // constructors
        // -------------------------------------------------------------------

        /// <summary>
        /// Construct a FactoredSegmenterCoder from config object.
        /// </summary>
        public FactoredSegmenterCoder(FactoredSegmenterCoderConfig config) :
            this(config.Model ?? FactoredSegmenterModel.Load(config.ModelPath))
        {
            checkEvery = config.CheckEvery;
        }

        // internal constructor from model object
        private FactoredSegmenterCoder(FactoredSegmenterModel model)
        {
            this.model = model;

            // instantiate underlying SPM coder
            if (!(bool)model.ModelOptions.UseSentencePiece) // @TODO: UseSentencePiece flag should go away; use model.SentencePieceModel == null
            {
                spmCoder = null;
                Logger.WriteLine("FactoredSegmenter: Not using SentencePiece");
            }
            // Review: Right now I'm setting the cache size to the shortlist vocab length.
            // I'm not sure what the best thing to do here is though, probably language dependent.
            // For FRA-ENU, 10K worked great; I'd expect the same to be true for all spacing languages, 
            // but what about non-spacing languages?
            else if (model.SentencePieceModel != null)
                spmCoder = new SentencePieceCoder(new SentencePieceCoderConfig
                {
                    SentencePieceModel = new SentencePieceModel(model.SentencePieceModel),
                    SplitCacheSize = model.ShortlistVocab?.Length ?? 0,
                    VocabSubset = model.KnownLemmas // in case SPM vocab contains extra pieces we did not capture from re-encoding the training set
                });
        }

        // -------------------------------------------------------------------
        // training (construct from data)
        // -------------------------------------------------------------------

        // this is an internal function that implements the externally-facing FactoredSegmenterModel.Train() API
        internal static FactoredSegmenterModel Train(FactoredSegmenterModelTrainConfig config,
            IEnumerable<string> input, // input lines to train vocab and SPM from
            IEnumerable<Dictionary<string, string>> sourceSentenceAnnotations, // optional: sentence-level annotations
            string fsmModelPath, string spmBinDir)
        {
            var model = new FactoredSegmenterModel(config.ModelOptions);
            Sanity.Requires(model.ModelOptions.UseSentencePiece == null, $"{nameof(model.ModelOptions.UseSentencePiece)} is an internal option that must not be specified by user");
            var spmConfig = config.SentencePieceTrainingConfig;
            model.ModelOptions.UseSentencePiece = spmConfig != null;
            if (spmConfig != null)
            {
                // encode all lines (lazily)
                var fsm1 = new FactoredSegmenterCoder(model);
                var tokenStrings = from line in input.AsParallel().AsOrdered()
                                   from tokenString in fsm1.EncodeForSPMTraining(line) // this partially encodes up to the point where SPM gets applied
                                   select tokenString;

                // some SentencePiece options would interfere with our operation, overwrite them
                Sanity.Requires(spmConfig.VocabSize != null, $"{nameof(spmConfig)}.{nameof(spmConfig.VocabSize)} vocabulary size must be specified");
                spmConfig.NormalizationRuleName  = SentencePieceNormalizationRuleName.Identity; // don't interfere with normalization
                spmConfig.AddDummyPrefix         = false; // don't interfere with spacing
                spmConfig.SplitByWhitespace      = false;
                spmConfig.RemoveExtraWhitespaces = false;
                spmConfig.InputSentenceSize      = int.MaxValue; // don't interfere with our own counting
                spmConfig.MiningSentenceSize     = int.MaxValue;
                spmConfig.TrainingSentenceSize   = int.MaxValue;
                spmConfig.SeedSentencepieceSize  = int.MaxValue;

                // run SentencePiece
                var tempSPMModelOutPath = fsmModelPath.Substring(0, fsmModelPath.Length - 3) + "spmtmp.model";
                model.SentencePieceModel = SentencePieceModel.Train(tokenStrings, tempSPMModelOutPath, spmConfig, config.MinPieceCount, spmBinDir).Bytes;
            }

            // determine the vocabulary
            // General format of lines:
            //   LHS [SYM RHS1 RHS2...]
            //   with SYM being : or <->
            //   and whole-line # comments and empty lines being skipped
            // Format (using example):
            //   # factors
            //   _lemma         # class of lemmas
            //   _gl            # class of factors of a given type
            //   gl+ : _gl      # names of factors
            //   gl- : _gl
            //   _has_gl        # trait for glue-left factor
            //   _c             # caps factor type
            //   ca : _c
            //   ci : _c
            //   cn : _c
            //   _has_c         # trait for caps factor
            //   # lemmas
            //   </s> : _lemma                  # this is a lemma with no factors
            //   APPLE : _lemma _has_gl _has_c  # this is a lemma and has gl and caps factors
            //   , : _lemma _has_gl             # punctuation
            //   # factor distributions
            //   _lemma <->         # associates a probability P({leaves of _lemma}|_lemma) with all leaf types that derive from _lemma (with leaf type names as surface forms)
            //   _c <-> _has_c      # associates P({"ca","ci","cn"}|_c) with leaves of _c that is only used for words that are of type _has_c
            //   _gl <-> _has_gl    # P({"gl+","gl-"}|_gl) only where _has_gl
            // Example words in the corpus:
            //   APPLE|gl-|cn
            //   APPLE|gl-|ci
            //   ,|gl+
            // Invalid examples:
            //   APPLE|gl-      # misses caps factor
            //   ,|ci           # punctuation has no caps factor
            // A few observations:
            //  - names without _ can occur in words, names with _ do not (by convention)
            //  - all first tokens are types.
            //    E.g. Apple:
            //     - APPLE is a type, which in turn is a _lemma, a _has_gl, and a _has_c
            //     - APPLE|gl-|cn is of all these types at once: APPLE, _lemma, gl-, _gl, cn, _c
            //  - lines that only contain a single token only define a type name
            //  - the probability of a word is the product of prob values of all of its leaf types
            //    E.g. APPLE|gl-|cn:
            //     - has 6 types: _lemma, _has_gl, _has_c (from APPLE), gl-, _gl (from gl-), as well as cn and _c (from cn)
            //     - APPLE, gl-, and cn are leaf types that have a probability distribution
            //     - the distributions that gl- and cn belong to are conditional on _has_gl and _has_c
            //     - since _has_gl_ and _has_c are base types of APPLE, these factors are valid, and their prob value is used
            //     - resulting prob = P(APPLE|_lemma) * P(gl-|_gl) * P(ci|_c)
            // Notes:
            //  - this somewhat complex spec is meant to allow conditional factors in a fine-grained fashion
            //  - however, for now, Marian will parse these in a hard-coded fashion by matching the _has_ prefix:
            //     - all types starting with _ except _has_* are factor names
            //     - X : _x makes surface form X part of prob distribution _x except for _has_*
            //     - X : _has_x adds factor "x" to lemma X
            //     - _x <-> form only allows "_x <->" or "_x <-> _has_x" (same x), and is otherwise unused
            //     - _lemma is special
            //     - single char items occuring fewer times than MinCharCount are dropped from the lemma set

            var fsm = new FactoredSegmenterCoder(model);

            // process sentence-level annotations
            // For each input sentence, sourceSentenceAnnotations contains a dictionary of
            // sentence-level annotations in the form of type-value pairs.
            // Since during training, the total set of annotation is are merely collected, we do
            // not actually require that the annotations match line by line.
            Dictionary<string, HashSet<string>> sourceSentenceAnnotationsDict = null; // [type] -> set of observed values
            if (sourceSentenceAnnotations != null)
            {
                var nonEmptyAnnotations = from a in sourceSentenceAnnotations where a.Any() select a;
                if (nonEmptyAnnotations.Any()) // at least one actual annotation is given
                {
                    sourceSentenceAnnotationsDict = CollectSentenceAnnotations(model,
                        from sourceSentenceAnnotation in sourceSentenceAnnotations
                        from kvp in sourceSentenceAnnotation
                        select (kvp.Key, kvp.Value));
                }
            }

            // helper function to determine factors for an example string
            // When determining factors for a class of words ({word} or serialized unrepresentable chars),
            // we avoid using long lists of if statements to cater to all conditions, because the factor set
            // depends on options. Instead, we determine factors by selecting one representative word per class,
            // encoding it, and using the resulting factor set.
            Factors GetFactorsFromExample(string s)
            {
                IEnumerable<Token> tokens = new Token[]{ new Token(s) };  // create token in same form as Pretokenize() would
                tokens = fsm.Factorize(tokens);  // determine factors
                Sanity.Requires(tokens.Count() == 1, "GetFactorsFromExample unexpectedly got an example that was split by Factorize??");
                return tokens.First().factors;   // get factors from the token
            }

            // determine the set of lemmas and their observed factor types
            var factorTypeMap = new Dictionary<string, FactorType[]>(); // [lemma] -> set of factor types
            void AddToFactorTypeMap(string lemma, FactorType[] factorTypes) // helper to remember the factor set of a lemma, with consistency check
            {
                if (!factorTypeMap.TryGetValue(lemma, out var foundFactorTypes))
                    factorTypeMap[lemma] = factorTypes;
                else // check that the type map is always the same for all instances of this lemma
                    Sanity.Requires(factorTypes.SequenceEqual(foundFactorTypes), $"Inconsistent factor types for lemma {lemma}: {" ".JoinItems(foundFactorTypes)} vs. {" ".JoinItems(factorTypes)}");
            }
            Logger.WriteLine("FactoredSegmenter: Collecting factor typemap...");
            // first, hard-code the special lemmas and factors for class types, as if we had seen them in data
            foreach ((var lemma, var example) in Token.ALL_CLASS_LEMMAS) // e.g. {word}|classPhraseFix|index042|wb|cn -> "Hello"
            {
                var factorTypes = GetFactorsFromExample(example).Types.ToList();
                factorTypes.Add(Factors.CLASS_TYPE);
                if (!model.ModelOptions.SerializeIndicesAndUnrepresentables)
                    factorTypes.Add(Factors.INDEX_TYPE); // index factor only if we don't serialize it into {digits}
                // if DistinguishInitialAndInternalPieces we generate a second form with _
                if (model.ModelOptions.DistinguishInitialAndInternalPieces && factorTypes.Contains(Factors.WORD_BEG_TYPE))
                {
                    factorTypeMap[Token.WORD_BEG_PREFIX + lemma] = factorTypes.ToArray();
                    factorTypes.Remove(Factors.WORD_BEG_TYPE);
                    factorTypes.Add(Factors.WORD_INT_TYPE); // the form without _ gets factor WORD_INT instead
                }
                factorTypeMap[lemma] = factorTypes.ToArray();
            }
            // encode all sentences fully for counting the lemmas and collecting their actually-used factors
            var encodedTokens = from line in input.AsParallel()
                                from tokenString in (fsm.EncodeWithLogging(line) as Encoded).encodedTokens
                                select tokenString;
            // now add all actually observed tokens, and derive from them what factors they take
            var lemmaCharCounts = new Dictionary<string, int>(); // [single-char lemma] -> count (char can be surrogate pair, hence using string type)
            foreach (var token in encodedTokens)
            {
                Sanity.Requires(!token.IsClass, "Training data contains classes??");
                var lemma = token.SubStringNormalizedForLemma(fsm.model.ModelOptions);
                var factorTypes = token.factors.Types;
                AddToFactorTypeMap(lemma, factorTypes.ToArray());
                if (lemma.IsSingleCharConsideringSurrogatePairs()) // count single-char lemmas
                {
                    lemmaCharCounts.TryGetValue(lemma, out var count);
                    lemmaCharCounts[lemma] = count + 1;
                }
            }
            // remove too rare single characters from the vocab
            // @TODO: Marian will presently map them to <unk>, but we should better encode them numerically.
            if (config.MinCharCount > 1)
            {
                int prevSize = factorTypeMap.Count();
                foreach (var kvp in lemmaCharCounts)
                {
                    if (kvp.Value < config.MinCharCount)
                        factorTypeMap.Remove(kvp.Key);
                }
                if (factorTypeMap.Count() < prevSize)
                    Logger.WriteLine($"FactoredSegmenter: Removed {prevSize - factorTypeMap.Count()} out of {prevSize} single-character lemmas that had fewer than {config.MinCharCount} observations");
            }
            // needed symbols for serialization of indices and/or index factors
            if (model.ModelOptions.SerializeIndicesAndUnrepresentables)
            {
                // lemmas for various classes of unencodable characters
                // These are examples of different character classes, that have different factor sets.
                // E.g. the Davanagaari character has no capitalization, while the Chinese character has
                // different boundary factors. The code below runs Factorize() over each character and
                // reads out the factor set. Note that the factor set also depends on configuration options.
                foreach (var ch in "a0.त超ⓐ☺") // one example for each class
                {
                    var factors = GetFactorsFromExample(ch.ToString());
                    string lemma = GetSerializedUnrepresentableLemma(factors); // and derive the lemma name which encodes the factors
                    AddToFactorTypeMap(lemma, factors.Types.ToArray());
                }
                // the parts of the numbers
                var factorTypesEmpty = new FactorType[0]; // serialized digits have no associated factors
                foreach (var lemma in Enumerable.Concat(Token.UNK_DIGITS, new[] { Token.UNK_DIGITS_END }))
                    AddToFactorTypeMap(lemma, factorTypesEmpty);
            }
            // early check for overflow of Marian's WordIndex type due to too many factors/values
            var allUsedFactorTypes = (from factorTypes in factorTypeMap.Values
                                      from factorType in factorTypes
                                      select factorType)
                                      .Distinct();
            // check for whether Marian would overflow with its 32-bit word-id type
            // WordIndex range is prod_t (1 + number of values for factor type t).
            var marianWordIdRange = (from factorType in allUsedFactorTypes select (long)factorType.Factors.Count() + 1).Aggregate(seed: 1, func: (long x, long y) => x * y);
            Logger.WriteLine($"FactoredSegmenter: Virtual Marian factor vocabulary size is {marianWordIdRange:#,##0}");
            Sanity.Requires(marianWordIdRange <= uint.MaxValue, $"Too many factors, virtual index space {marianWordIdRange:#,##0} exceeds the bit limit of Marian's WordIndex type");
            // create the lines of the vocab file
            Logger.WriteLine("FactoredSegmenter: Creating vocab file...");
            var vocabLines = new List<string>();
            // factors
            vocabLines.Add("# factors"); // (comments and blank lines for human readability only)
            vocabLines.Add("");
            vocabLines.Add("_lemma");
            foreach (var t in allUsedFactorTypes.OrderBy(t => t.ToString(), StringComparer.Ordinal)) // sorted alphabetically
            {
                var type = t.ToString();
                Sanity.Requires(!Token.NeedsEscaping(type), "Token type name must not contain escapable chars");
                vocabLines.Add("");
                vocabLines.Add($"_{type}");                 // the factor class name, e.g. _gl
                vocabLines.AddRange(
                    from f in t.Factors
                    orderby t.ToString()                    // (sort to remove arbitrariness of source-data ordering only)
                    select $"{f.Serialized} : _{type}");    // e.g. gl+ : _gl
                vocabLines.Add($"_has_{type}");             // the trait to select this factor, e.g. _has_gl
            }
            // lemmas
            vocabLines.Add("");
            vocabLines.Add("# lemmas");
            vocabLines.Add("");
            var specialTokens = new List<string> { "<unk>", "<s>", "</s>" }; // Flo expects these tokens at these vocab positions
            if (model.ModelOptions.InlineFixes && model.ModelOptions.InlineFixUseTags)
                specialTokens.AddRange(new string[] { INLINE_FIX_SRC_ID_TAG, INLINE_FIX_TGT_ID_TAG, INLINE_FIX_END_ID_TAG });
            if (sourceSentenceAnnotationsDict != null) // add vocab entries for sentence-level annotations, such as <SLA:target_language=ENU>
            {
                foreach (var key in sourceSentenceAnnotationsDict.Keys)
                    foreach (var value in sourceSentenceAnnotationsDict[key])
                        specialTokens.Add(ToSentenceAnnotationTokenString(key, value));
            }
            Sanity.Requires(specialTokens.All(s => !factorTypeMap.ContainsKey(s)), $"Segmented input data contains an invalid (reserved) token ({", ".JoinItems(specialTokens)})??");

            Logger.WriteLine($"These are all special token we will have: '{String.Join("\t", specialTokens)}'");

            vocabLines.AddRange(from s in specialTokens
                                select $"{s} : _lemma");
            var lemmaTokens = factorTypeMap.Keys.OrderBy(lemma => lemma, StringComparer.Ordinal);
            vocabLines.AddRange(from lemma in lemmaTokens // add lemmas lexicographically sorted
                                let factorTraits = from t in factorTypeMap[lemma] select $"_has_{t}"
                                select $"{Token.SerializeLemma(lemma)} : _lemma {" ".JoinItems(factorTraits)}"); // e.g. APPLE : _lemma _has_gl _has_c
            // factor distributions
            vocabLines.Add("");
            vocabLines.Add("# factor distributions");
            vocabLines.Add("");
            vocabLines.Add("_lemma <->");
            vocabLines.AddRange(from t in allUsedFactorTypes
                                select $"_{t} <-> _has_{t}"); // e.g. _gl <-> _has_gl
            model.FactorSpec = vocabLines.ToArray();

            // create lemma vocab for detecting unencodable characters
            var lemmaVocab = specialTokens.Concat(lemmaTokens);
            model.KnownLemmas = new HashSet<string>(lemmaVocab);

            // create shortlist vocab
            // Currently, that is just the lemma vocab, but in serialized (escaped) form.
            model.ShortlistVocab = specialTokens
                                   .Concat(from lemma in lemmaTokens
                                           select Token.SerializeLemma(lemma).ToString())
                                   .ToArray();

#if True    // self-check of legacy function --leave this in for a while. Bug #100736
            var shortlistVocab2 = LegacyRecoverLemmaVocabFromFactorSpec(model.FactorSpec);
            Sanity.Requires(shortlistVocab2.SequenceEqual(model.ShortlistVocab), "LegacyRecoverLemmaVocabFromFactorSpec did not recover ShortlistVocab correctly??");
#endif

            return model;
        }

        // helper to re-generate the lemma vocab for older .fsm model files that did not have it stored in them
        internal static string[] LegacyRecoverLemmaVocabFromFactorSpec(string[] factorSpec)
        {
            // parse the factor spec
            // This is not a full parser. Since the factor spec is written by this Train() above,
            // we know exactly what features of the spec format are used; and we only understand those.
            // This will create exactly the same as the expression in Train() for model.ShortlistVocab.
            return (from line in factorSpec
                    let tokens = line.Split(' ')
                    where tokens.Length >= 3 && tokens[1] == ":" && tokens[2] == "_lemma" //  XYZ : _lemma
                    select tokens[0]) // note: must keep it in serialized form
                   .ToArray();
        }

        public override string[] ShortlistVocab => model.ShortlistVocab;

        // convert a serialized token into the shortlist form
        // Presently, shortlist form == the lemma in serialized (escaped) form.
        // This function must match the format in model.ShortlistVocab.
        // Tokens that are out of vocabulary are acceptable, though.
        public override string TranscodeTokenToShortlist(string tokenString)
        {
            var token = Token.Deserialize(tokenString, model.ModelOptions);
            return token.Serialize(model.ModelOptions, withoutFactors: true);
            // @TODO: see whether this is notably slower than short-circuiting it as
            //token.Substring(0, token.IndexOf(Token.CLASS_SEPARATOR));
        }

        // -------------------------------------------------------------------
        // encoding
        // -------------------------------------------------------------------

        // split a sequence of tokens, according to a lambda that is called for each within-token
        // character pair to determine whether the token should be split
        IEnumerable<Token> SplitTokens(IEnumerable<Token> tokens, Func<string, int, bool> ShouldSplitFunc)
        {
            // this function is elegant to write with LINQ (cf. below), but manually unrolled for speed
            var res = new List<Token>(200);
            foreach (var token in tokens)
                token.SplitAndAddTo(ShouldSplitFunc, res);
            return res;
        }

        readonly IEnumerable<string> emptyStringArray = new string[] { };

        // determine the capitalization factor (all-lower, all-upper, cap-initial, or null if not a word) for a token
        Factor CapitalizationFactorsFor(Token token) // or null if none
        {
            if (token.factors.spanClass != null) // spanClass, e.g. phrase fix. For now don't say it should capitalize anything.
                return Factors.CAP_NONE;         // Note that spanClass for now always maps to {word}. Other potential future mappings, e.g. {punctuation}, would have no cap factor.
#if true    // @BUGBUG: empty tokens have no cap factor. Legacy code used CAP_NONE. Hence, we cannot change it without retraining.
            else if (token.Length == 0 &&          // empty string
                     !model.ModelOptions.DistinguishInitialAndInternalPieces &&                  // (we have no legacy models with these flag set,
                     !model.ModelOptions.UseContextDependentSingleLetterCapitalizationFactors && // so it's OK to not emulate the bug)
                     !model.ModelOptions.SingleLetterCaseFactors &&
                     !model.ModelOptions.SerializeIndicesAndUnrepresentables)
                return Factors.CAP_NONE;
#endif
            else if (token.NeedsCapsFactor()) // token is of word nature and has a least one bicameral letter (e.g. not numbers or CJK)
            {
                var c0 = token.First();
                if (token.Length > 1 && token.HasAndIsAllCaps()) // note: single-letter tokens are CAP_INITIAL, which has a higher prior
                    return Factors.CAP_ALL; // note: if begins with combiner, then [if all non-combiners are all-caps, then CAP_ALL else CAP_NONE]
                else if (token.Length == 1 && model.ModelOptions.SingleLetterCaseFactors)
                    return c0.HasAndIsUpper() ? Factors.SINGLE_CAP_UPPER : Factors.SINGLE_CAP_LOWER;
                else if (c0.HasAndIsUpper())
                    return Factors.CAP_INITIAL;
                else
                    return Factors.CAP_NONE;  // note: also applies if c0=='ß'
            }
            else
                return null; // no cap factors for non-words (incl. e.g. "ⓐⓝⓓⓡⓔⓨ"), non-bicameral scripts, all-combiner tokens
        }

        // create a set of tokens from the source line and the annotated spans
        // Tokens that were replaced by a class had their string set to empty, which shields them from further processing.
        private (IEnumerable<Token> Tokens, Dictionary<int, string> DecodeAsTable)
            TokenizeBySpans(Token wholeLineToken, List<AnnotatedSpan> annotatedSpans, int seed)
        {
            Dictionary<int, string> decodeAsTable;
            var res = new List<Token>();
            if (annotatedSpans != null)
            {
                // convert spans into tokens
                // No check for gaps or overlap at this point in time.
                // HTML tags have ClassType == null && EncodeAsIf == "". This will cause them to be stripped out of the tokenized output.
                var random = new Random(seed);
                var indicesSeen = new HashSet<int>();
                decodeAsTable = new Dictionary<int, string>();
                foreach (var span in annotatedSpans)
                {
                    // get a unique random index for class spans
                    // By using the same seed, source and target get matching ids.
                    // The caller must ensure that their annotatedSpans match in terms
                    // of having a class type and the class type itself.
                    // @BUGBUG? Why do we allocate indices for non-class spans? Is that needed, e.g. to somehow keep src and tgt in sync?
                    var allocateIndex = span?.ClassType != null;
                    var index = -1;
                    if (allocateIndex)
                    {
                        var numRepresentableIndices = Factors.INDEX_FACTORS.Length;
                        index = random.Next(maxValue: numRepresentableIndices); // "range of return values includes 0 but not maxValue"
                        while (indicesSeen.Contains(index) && indicesSeen.Count < numRepresentableIndices) // find next slot
                            index = (index + 1) % numRepresentableIndices;
                        if (indicesSeen.Count >= numRepresentableIndices) // too many spans: ignore
                            continue;
                        indicesSeen.Add(index);
                    }

                    // skip empty spans
                    if (span == null)
                        continue;

                    // form token, which refers to the source range
                    var token = wholeLineToken.Narrow(span.StartIndex, span.Length); // cut out the token's range

                    // encode it as this character sequence instead (keep outer range though)
                    if (span.EncodeAsIf != null)
                    {
                        Sanity.Requires(span.ClassType == null, "Cannot have a ClassType on a span with EncodeAsIf");
                        token = token.OverrideAsIf(span.EncodeAsIf);
                        res.Add(token);
                    }

                    // If classType == null, we don't need to allocate an index factor. Notably, this will be null for HTML tags, and so
                    // they will not occupy one of the slots for index factors. --@BUGBUG: But we still allocate one. Why?
                    else if (span.ClassType != null)
                    {
                        // remember decode string           
                        var decodeAs = span.DecodeAs ?? token.Word; // default to original string, or if given, EncodeAsIf
                        Sanity.Requires(decodeAs != "", "DecodeAs string for phrase fixes must not be empty"); // would otherwise cause spacing problems in output

                        if (!model.ModelOptions.InlineFixes) // not inline: replace by class token
                        {
                            decodeAsTable[index] = decodeAs;
                            token = token.OverrideAsIf(""); // empty string will exclude this token from all subsequent steps
                            token.factors.spanClass = Factors.CLASS_FACTORS[(AnnotatedSpanClassType)span.ClassType];
                            token.factors.index = Factors.INDEX_FACTORS[index]; // note: spanClass factor always implies an index factor as well
                            res.Add(token);
                        }
                        else if (span.DecodeAs != null) // inline fix in source: replace by <abc|XY>  --@TODO: distinguish whether we want to keep the source or not
                        {
                            var srcToken = token.PseudoTokenAt(token.Word, after: false); // original is aligned to an empty string
                            var tgtToken = token.OverrideAsIf(decodeAs); // target token's character range covers the full phrase-fixed source range
                            srcToken.factors.inlineFix = Factors.INLINE_FIX_WHAT;
                            tgtToken.factors.inlineFix = Factors.INLINE_FIX_WITH;
                            // Note: At this point, we leave null all that should be INLINE_FIX_NONE. That will be updated a late stage.
                            res.Add(srcToken);
                            res.Add(tgtToken);
                        }
                        else // inline fix in the target sentence
                        {
                            // We recognize the target side in training by span.DecodeAs being null.
                            // @TODO: Make this condition explicit; now we just know because we know how we are called.
                            var tgtToken = token.OverrideAsIf(token.Word); // for better alignment, force to cover char range even if broken by SPM
                            if (!model.ModelOptions.InlineFixUseTags)
                                tgtToken.factors.inlineFix = Factors.INLINE_FIX_WITH; // use the same factor for target as in the source
                            res.Add(tgtToken);
                        }
                        // On choice of character range for inline fixes:
                        //  - source side / inference:
                        //     - the inline-fixed source are reduced to a 0-length source range at the start of the fixed source word
                        //     - the target token's replacement's character range is that of the full source token's range
                        //       Note that the replacement string does not exist in the source, so there is no
                        //       way to refer to the replacement string itself via a character range.
                        //  - target side:
                        //     - character range is manipulated such that even if it gets SPM-broken,
                        //       each of those pieces will have a source range equal to the full phrase-fixed source phrase.
                        //  - effect on alignment:
                        //     - The input to the alignment process for pieces is based on the source ranges of
                        //       the tokens fed to Marian for training. Tokens with empty source ranges get deleted.
                        //       The source token has an empty source range, and therefore gets deleted.
                        //       Hence, the aligner will only get to see the target phrase on both sides. This should make
                        //       alignment robust. The token's alignment is that of the entire source phrase.
                        //       If it gets SPM-broken, each SPM-broken token still retains
                        //       the full source phrase's character range (the ranges are not split, since
                        //       the token no longer refers to individual characters of the source string).
                        //     - The resulting alignment will then be mapped to Marian-level tokens indices.
                        //       This is done by first mapping the index-based alignment from the aligner
                        //       to a character-range alignment. An inline-fixed phrase will now align
                        //       to the source phrase (as a whole). Then, the character-range alignment will be
                        //       mapped to indices into the actual Marian tokens. For inline-fixed phrases,
                        //       that character range matches that of the Marian tokens of the replacement
                        //       phrase, not the source.
                        //       The mapping will never assign any alignment to tokens with 0 source range,
                        //       as these are unseen by the aligner.
                        //     - If either source or target phrase are SPM-broken, the mapping step will align all
                        //       pieces of the target phrase to the full source character range identically.
                        // This specific choice was made to get these behaviors for consumers of alignments:
                        //  - regular decoding (alignment result):
                        //     - The target replacement (ideally) gets copied through, piece by piece. Marian decoder will
                        //       (ideally) align each resulting piece in the output to the Marian tokens of the
                        //       replacement pieces in the source (not the source tokens, since cross-attention
                        //       is not allowed to attend to those). This is desired.
                        //     - For the API's return value, these get translated into source character ranges.
                        //       The source range for reach of the target replacement pieces in the source
                        //       is that of the original phrase-fixed phrase. Hence, each output piece will align
                        //       to the full character range of the original source phrase, which is the expected behavior.
                        //  - shortlist creation:
                        //     - The shortlist will contain NO entries from the phrase-fixed source
                        //       (even if Marian gets to see it), because the source phrase has a source range
                        //       of 0.
                        //       It will contain entries for all pieces of the target translating to themselves (desired
                        //       for copy-though) as well as all other pieces in the same target phrase (undesired but not harmful).
                        //       Pseudo tokens to delimit source and target ranges are excluded from the shortlist, as they have a
                        //       empty source range.
                        //  - guided alignments
                        //     - Tokens with empty source ranges, like pseudo tokens and phrase-fixed source ranges,
                        //       are never aligned to in the guided alignments, since the aligner does not get to see
                        //       them because they have an empty character range. Phrase-fix outputs are aligned to their
                        //       (replaced) source tokens (if SPM'ed, then all pieces align to all). This is desired
                        //       (except for the all-to-all expansion). Note that this is consistent with Marian,
                        //       which is prevented by code to *cross*-attend to the phrase source tokens.
                    }
                }
                // sort by increasing start index. Note: must be stable sort (OrderBy, not Sort) to inline phrase-fixes in order.
                int CompareTokens(Token a, Token b)
                {
                    int cmp = a.OrigRange.StartIndex.CompareTo(b.OrigRange.StartIndex);
                    if (cmp == 0)
                        cmp = a.OrigRange.Length.CompareTo(b.OrigRange.Length);
                    return cmp;
                }
                res = res.OrderBy(token => token, Comparer<Token>.Create(CompareTokens)).ToList();
            }
            else
                decodeAsTable = null;
            // cover gaps and check for overlap
            for (int i = 0; i <= res.Count/*this changes*/; i++)
            {
                int prevEnd = i == 0 ? 0 : (res[i - 1].OrigRange.StartIndex + res[i - 1].OrigRange.Length);
                int thisStart = i < res.Count ? res[i].OrigRange.StartIndex : wholeLineToken.Length;
                Sanity.Requires(thisStart >= prevEnd, $"Annotated spans overlap (prev end: {prevEnd}; next start: {thisStart}"); // note: zero-length spans are allowed
                if (thisStart > prevEnd) // gap
                {
                    var token = wholeLineToken.Narrow(prevEnd, thisStart - prevEnd);
                    res.Insert(i, token); // this shifts all indices, so i++ goes to the same list item
                    continue;
                }
                // else leave the list entry as is
            }
            return (res, decodeAsTable);
        }

        // This function is part of Encode(). It performs all steps of encoding before breaking
        // a token into pieces via SentencePiece, including:
        //  - breaking at annotated-spans boundaries
        //  - breaking at unambiguous word boundaries (that can be decided without a model)
        //  - break at FactoredSegmenter boundaries (e.g. split numerals into digits, split mixed-case words)
        // This is a separate function because these steps also need to be executed during SentencePiece training.
        // This function does NOT set any factors except factors for annotated classes (index or inline).
        // This function does NOT change the string except for AnnotatedSpan replacements.
        // ...and a hard-coded pre-replacement of \u2581 which we currently can't handle otherwise. Needs to be fixed.
        private (IEnumerable<Token> Tokens, Dictionary<int, string> DecodeAsTable, Token wholeLineToken)
            Pretokenize(ref string line, List<AnnotatedSpan> annotatedSpans = null, int? seed = null)
        {
#if True    // Workaround: since DistinguishInitialAndInternalPieces is experimental, we kept the code
            // simple and did not handle the _ prefix itself correctly. For now, we just hard-map it to ASCII _.
            // Note that this makes FactoredSegmenter non-reversible for this one case.
            if (model.ModelOptions.DistinguishInitialAndInternalPieces)
                line = line.Replace(Token.WORD_BEG_PREFIX[0], '_');

            // Workaround: SentencePiece cannot encode its space character ("\u2581").
            // @TODO: Add a way to escape at this stage. We may need it also for other things.
            if (line.Contains('\u2581'))
                line = line.Replace('\u2581', '_');
            // Note that the above makes FactoredSegmenter non-reversible for these cases.

            // If we find a better solution, we shall remove the 'ref' from the line parameter.
#endif
            // start with the full line
            var wholeLineToken = new Token(line);

            // get tokens from annotated spans first (will return the entire line if no spans are given)
            var (tokens, decodeAsTable) = TokenizeBySpans(wholeLineToken, annotatedSpans, seed ?? 1);

            // - remove zero-length tokens, i.e. annotatedSpans of length 0 or EncodeAsIf="" that will not show as class tokens
            // - tokenize (simplistic high-level word breaker at script and character-type changes)
            tokens = from token in tokens
                     where token.Length > 0 || token.IsClass
                     from splitToken in token.SplitToken(ScriptHelpers.DetectUnambiguousWordBreaks(token.Word))
                     select splitToken;

            // - FS tokenization
            //    - break at additional changes of character type (space, punct) (we already broke at script changes)
            //    - break digit strings into one-char sequences (note: also for Chinese numerals... other languages?)
            //    - spaces and non-printing chars (all chars <= 0x20) are kept as a single-char strings
            //    - break XYz -> X Yz, break xY -> x Y
            //    - SplitHan mode: break all Han characters into single chars
            tokens = SplitTokens(tokens, (string s, int pos) =>
            {
                var c1 = s[pos];
                var c0 = s[pos - 1];
                if (c0 <= ' ' || c1 <= ' ') // space proper and non-printing
                    return true;
                if (Token.IsSpecialTokenDelimiter(c0) || Token.IsSpecialTokenDelimiter(c1)) // < and { are reserved for special-token syntax, except if they occur as single chars
                    return true;
                // split on designation change, with special handling of combining marks
                // @TODO: We also should handle surrogate pairs here. Only needed for designation changes.
                var d1 = c1.GetUnicodeMajorDesignationWithOurSpecialRules();
                var d0 = c0.GetUnicodeMajorDesignationWithOurSpecialRules();
                if (d0 == 'N' || d1 == 'N') // break digits. Note: decimal point and comma separator are now treated as number boundaries
                    return true;
                else if (d0 != d1) // change of character type
                    return true;
                // split Han characters (SplitHan only)
                if (model.ModelOptions.SplitHan && Unicode.GetScript(c0) == Unicode.Script.Han && Unicode.GetScript(c1) == Unicode.Script.Han)
                    return true;
                // casing change
                // @TODO: Do we ever get a string where some letters have caps and some don't? Since we split by script, and this
                // property is one of script, we should not.
                if (pos + 1 < s.Length && s.HasAndIsUpperAt(pos - 1) && s.HasAndIsUpperAt(pos) && s.HasAndIsLowerAt(pos + 1)) // XYz -> X Yz
                    // @BUGBUG: This does not correctly handle combiners
                    return true;
                else if (s.HasAndIsLowerAt(pos - 1) && s.HasAndIsUpperAt(pos)) // xY -> x Y
                    // @BUGBUG: This does not correctly handle combiners
                    return true;
                else
                    return false;
            });
            return (tokens, decodeAsTable, wholeLineToken);
        }

        // convert a token sequence into a tuple sequence (prefix, token) for DistinguishInitialAndInternalPieces mode.
        // The prefix flag says whether a _ gets prepended to the lemma.
        // If that mode is not enabled, it returns false for all.
        IEnumerable<(Token token, bool lemmaWordBegPrefix)> TokensWithLemmaWordBegPrefix(IEnumerable<Token> tokens)
        {
            // optionally distinguish pieces at word start
            // (not applied to continuous scripts, numbers, punctuation)
            // We want that word-initial and word-internal pieces do not share the same factor embedding.
            // For that purpose, we
            //  - tell SPM to prepend a _ to the word, and include it in the process
            //    This makes SPM segment word-initial pieces differently from word-internal.
            //    Note, however, that for us, SPM only gives cut points, and from the cut points
            //    (and associated source substrings) alone, we cannot yet distinguish the same
            //    character sequence used both word-initially and word-internally.
            //  - mark such tokens in the serialized form, also by prepending a _
            //    Now we can also distinguish them in FactoredSegmenter.
            // This function determines whether a _ should be prepended. It returns a
            // sequence of tuples (original token, flag whether to prepend _).
            // The returned tokens are not modified, the caller is meant to apply the _ changes
            // on-the-fly as needed according to the returned flags.
            if (model.ModelOptions.DistinguishInitialAndInternalPieces)
            {
                var tokenArray = tokens.ToArray();
                for (int i = 0; i < tokenArray.Length; i++)
                {
                    // @TOOD: make prevIsWord stateful, implement a Scan() function, then use that here
                    // This function is called before the WORD_BEG factor exists.
                    // The following logic matches 100% the logic to decide WORD_BEG in Factorize().
                    int iPrev = i - 1;
                    if (model.ModelOptions.InlineFixes && tokenArray[i].factors.inlineFix == Factors.INLINE_FIX_WITH) // skip over inserted source/targets
                        while (iPrev >= 0 && tokenArray[iPrev].factors.inlineFix == Factors.INLINE_FIX_WHAT)
                            iPrev--;
                    bool prevIsWord         = iPrev >= 0 && tokenArray[iPrev].IsOfWordNature;
                    bool thisIsWord         =               tokenArray[i].IsOfWordNature;
                    bool isContinuousScript = thisIsWord && tokenArray[i].IsContinuousScript;
                    bool isWordBeg = thisIsWord && !prevIsWord; // same condition as in Factorize()
                    var lemmaWordBegPrefix = isWordBeg && !isContinuousScript;
                    yield return (tokenArray[i], lemmaWordBegPrefix);
                }
            }
            else
                foreach (var token in tokens)
                    yield return (token: token, lemmaWordBegPrefix: false);
        }

        // encode a line into the tokens that the SentencePiece training should see,
        // in the form it should see them
        private IEnumerable<string> EncodeForSPMTraining(string line)
        {
            try
            {
                var tokens = Pretokenize(ref line).Tokens;
                return from tf in TokensWithLemmaWordBegPrefix(tokens)
                       let token = tf.token
                       let lemmaWordBegPrefix = tf.lemmaWordBegPrefix
                       where token.First() > ' ' // (we have explicit space characters that should be excluded here)
                       select token.SubStringNormalizedForSPM(lemmaWordBegPrefix);
            }
            catch (Exception)
            {
                Logger.WriteLine($"EncodeForSPMTraining() failed with this input: {line}");
                throw;
            }
        }

        /// <summary>
        /// Opaque object to pass information from Encode() to Decode()
        /// </summary>
        internal class DecoderPackage : IDecoderPackage
        {
            internal Encoded Encoded { get; set; } // original token-index indexable encoded tokens, for alignment lookup and mapping

            internal Dictionary<int, string> DecodeAsTable { get; set; } // table for back-translating class tokens
        }

        /// <summary>
        /// See documentation of IEncoded.
        /// </summary>
        public class Encoded : IEncoded
        {
            public override string OriginalSourceText { get; }
            public override EncodedSegmentReference[] OriginalSourceTextSegments { get; }
            public override Dictionary<string, string> OriginalSourceSentenceAnnotations { get; }
            public override IEnumerable<string> TokenStrings => from token in encodedTokens select token.Serialize(modelOptions);
            public override IEnumerable<string> TokenStringsForAligner => from token in encodedTokens select token.Serialize(modelOptions, withoutFactors: true);
            public override List<ProcessedToken> ProcessedTokens =>
                (from token in encodedTokens
                 select ProcessedToken.CreateRegularToken(
                     sourceWord: token.Serialize(modelOptions),         // Marian-internal string representation of the token
                     origSource: new List<string> { token.OrigString }, // characters from the original string (may be empty in special cases)
                     rawCharStart: token.OrigRange.StartIndex,          // and their coordinates in the original string
                     rawCharLength: token.OrigRange.Length)).ToList();
            public override int Count => encodedTokens.Length;
            public override IDecoderPackage DecoderPackage { get; }

            internal readonly Token[] encodedTokens; // (this is read directly from the Train() function, which needs access to our internal Token type)
            internal readonly FactoredSegmenterModelOptions modelOptions;
            internal Encoded(string s, Dictionary<string, string> sourceSentenceAnnotations, IEnumerable<Token> e, FactoredSegmenterModelOptions modelOptions, IDecoderPackage decoderPackage)
            {
                OriginalSourceText = s;
                OriginalSourceSentenceAnnotations = sourceSentenceAnnotations;
                encodedTokens = e.ToArray();
                this.modelOptions = modelOptions;
                DecoderPackage = decoderPackage;

                // verify that all tokens actually refer to same source text (otherwise alignment info would be invalid)
                Sanity.Requires(encodedTokens.All(t => t.OrigLineIs(s)),
                                "Encoded() requires that all tokens refer to the original input string" );

                //@TODO: this monstrosity is a result of refactoring; should be simplified
                var encodedTokensSelfLinks = // fake links that link each token to its own source range
                    encodedTokens.Select((token, index) =>
                                         new List<DecodedSegment.SourceLink> { new DecodedSegment.SourceLink
                                         {
                                             SourceSegment = new EncodedSegmentReference
                                             {
                                                 RawSourceText = OriginalSourceText,
                                                 StartIndex = token.OrigRange.StartIndex, // token aligns to itself in Encode()
                                                 Length = token.OrigRange.Length,
                                                 IsWordTokenStart = false, // (not used by DecodeIntoConsecutiveSegments())
                                                 IsWordTokenEnd = false,
                                                 IsSpacingWordStart = false,
                                                 IsSpacingWordEnd = false
                                             },
                                             Confidence = 1  // @TODO: is this a probability?
                                         }});
                OriginalSourceTextSegments =
                    (from seg in DecodeIntoConsecutiveSegments(encodedTokens, encodedTokensSelfLinks.ToArray(), decodeAsTable: null)
                     where seg.SourceAlignment != null // this removes exactly the tokens that DecodeIntoConsecutiveSegments() inserted
                     select new EncodedSegmentReference
                     {
                         RawSourceText = seg.SourceAlignment.First().SourceSegment.RawSourceText,
                         StartIndex = seg.SourceAlignment.First().SourceSegment.StartIndex, // token aligns to itself in Encode()
                         Length = seg.SourceAlignment.First().SourceSegment.Length,
                         IsWordTokenStart = seg.IsWordTokenStart, // this was determined by DecodeIntoConsecutiveSegments()
                         IsWordTokenEnd = seg.IsWordTokenEnd,
                         IsSpacingWordStart = seg.IsSpacingWordStart,
                         IsSpacingWordEnd = seg.IsSpacingWordEnd
                     }).ToArray();
                Sanity.Requires(encodedTokens.Length == OriginalSourceTextSegments.Length, "Spaces somehow not correctly added and removed again??");
                // check rough left-to-right property
                // This check is an attempt to codify what is presently true about the ordering,
                // and to encourage to keep it that way. However, if you find that it fails, it may
                // well be that the code is right, and that this check is wrong.
                // If you relax this check, please make sure that it continues to be consistent with
                // the comment on IEncoded.OriginalSourceTextSegments.
                bool IsSegmentOrderValid((EncodedSegmentReference, EncodedSegmentReference) t)
                    => t.Item1.StartIndex + t.Item1.Length <= t.Item2.StartIndex || // strictly monotonous and non-overlapping
                       t.Item1.StartIndex <= t.Item2.StartIndex + t.Item2.Length;    // if not monotonous, at least require them to touch (one character overlap would be nice, but this interferes with numeric character encoding
                for (int i = 1; i < OriginalSourceTextSegments.Length; i++)
                {
                    if (!IsSegmentOrderValid((OriginalSourceTextSegments[i - 1], OriginalSourceTextSegments[i])))
                        Console.WriteLine($"Invalid order at token {i} {OriginalSourceTextSegments[i - 1]}({OriginalSourceTextSegments[i - 1].StartIndex},{OriginalSourceTextSegments[i - 1].Length}) vs {OriginalSourceTextSegments[i]}({OriginalSourceTextSegments[i].StartIndex},{OriginalSourceTextSegments[i].Length})");
                }
                Sanity.Requires(OriginalSourceTextSegments.Length == 0 || OriginalSourceTextSegments.Bigrams().All(IsSegmentOrderValid),
                                "Encoded tokens were found to have gotten into an unexpected order");
            }
        }

        // set the capitalization factor for every entry in the tokenArray, e.g. "HELLO" -> HELLO|ca
        private void SetCapitalizationFactors(Token[] tokenArray)
        {
            for (var i = 0; i < tokenArray.Length; i++)
            {
                var factor = CapitalizationFactorsFor(tokenArray[i]);
                Sanity.Requires(tokenArray[i].factors.cap == null && tokenArray[i].factors.singleCap == null, "Capitalization factors were unexpectedly set already");
                if (factor != null)
                    if (factor.type == Factors.SINGLE_CAP_TYPE)
                        tokenArray[i].factors.singleCap = factor;
                    else
                        tokenArray[i].factors.cap = factor;
            }
        }

        // apply reversible FactoredSegmenter transformations to pre-segmented input
        // Upon entring, the token sequence still covers the entire string, with "glue" assumed
        // between any two consecutive tokens. Spaces are still actual tokens.
        // This function's job is to
        //  - add all factors (except index  --@TODO: may move into a serialized form anyway)
        //  - elide space tokens (by encoding space information in surrounding token factors)
        //  - other transformations such as unencodables and class-token index
        private IEnumerable<Token> Factorize(IEnumerable<Token> tokens)
        {
            // fix unencodables; that is, anything that Marian would consider <unk>
            //foreach (var token in tokens)
            //    CheckForUnencodable(token);

            // - FS case factors
            //    - add token-level factors for init-caps and all-caps
            var tokenArray = tokens.ToArray();
            SetCapitalizationFactors(tokenArray);

#if true    // workaround for Bug #101419 "Training of allcaps factors is inconsistent"
            if (model.ModelOptions.UseContextDependentSingleLetterCapitalizationFactors)
                MakeContextDependentSingleLetterCapitalizationFactors(tokenArray);
#endif

            // - FS glue factorization
            //    - tokens are of two natures: word (incl. numerals and CJK) and punctuation (incl. space)
            //       - a "Word" carries "word boundary" flags
            //       - a "Punc" carries "glue" flags, whether there is a space next to them or not
            //       - a space is implied and therefore not its own token between...
            //          - two "Words"
            //          - a "Word" and a "Punc" unless glue flag is set
            //          - two "Puncs" unless at least one glue flag is set
            //          - note: for multiple consecutive spaces, only every second  can be implied
            //    - so far, spaces are single-character tokens, and all tokens are just consecutive (glued)
            var tokenList = new List<Token>();
            int iLastNonElidedSpace = -1; // index i of last emitted token, equal to i-1 except after an elided space
            for (int i = 0; i < tokenArray.Length; i++)
            {
                var iPrev = i - 1;
                var iNext = i + 1;

                if (model.ModelOptions.InlineFixes) // skip over inserted source/targets
                {
                    if (tokenArray[i].factors.inlineFix == Factors.INLINE_FIX_WITH)
                        while (iPrev >= 0 && tokenArray[iPrev].factors.inlineFix == Factors.INLINE_FIX_WHAT)
                            iPrev--;
                    else if (tokenArray[i].factors.inlineFix == Factors.INLINE_FIX_WHAT)
                        while (iNext < tokenArray.Length && tokenArray[iNext].factors.inlineFix == Factors.INLINE_FIX_WITH)
                            iNext++;
                    // @BUGBUG: This does not correctly set the iLastNonElidedSpace flag!
                }

                bool thisIsNotFirst = iPrev >= 0;
                bool thisIsNotLast  = iNext < tokenArray.Length;
                bool nextIsNotLast  = i + 2 < tokenArray.Length;

                bool prevIsWord = thisIsNotFirst && tokenArray[iPrev].IsOfWordNature;
                bool thisIsWord =                   tokenArray[i    ].IsOfWordNature;
                bool nextIsWord = thisIsNotLast  && tokenArray[iNext].IsOfWordNature;

                // "Punc": set glue factors and elide space
                if (!thisIsWord)
                {
                    bool thisIsSpace =                  tokenArray[i    ].IsSpace;
                    bool nextIsSpace = thisIsNotLast && tokenArray[iNext].IsSpace;

                    // "Punc" tokens that are space (equal to " ") are elided whereever they can be represented by factors.
                    // That is the case everywhere except:
                    //  - at sentence beginning or end, since spaces are only implied *between* tokens
                    //  - following an already elided space, since a second elided space cannot be represented by factors
                    // In these exceptions, a space is just another "Punc" token (it will be serialized in escaped form: "\x20").
                    bool prevIsElidedSpace = (iLastNonElidedSpace != iPrev); // last token was an elided space
                    bool thisIsElidedSpace = thisIsSpace && thisIsNotFirst && thisIsNotLast && !prevIsElidedSpace;
                    bool nextIsElidedSpace = nextIsSpace &&                   nextIsNotLast && !thisIsElidedSpace; // same logic as thisIsElidedSpace, shifted by 1

                    if (!thisIsElidedSpace) // any implied space is now represented by factors, so we must not have an explicit token anymore
                    {
                        // glue flags
                        // Unless there is an elided space involved, the glue flags are 'true', to represent that all tokens
                        // are just consecutive substrings. (With one exception: They are 'false' at the sentence start and end.)
                        // Only if we elide a space, we will ever set a glue flag to 'false', which indicates an implied space.
                        bool glueRight = thisIsNotLast  && !nextIsElidedSpace; // if next is space, elide it and indicate by no-glue flag
                        bool glueLeft  = thisIsNotFirst && !prevIsElidedSpace; // if prev was elided space, also set the no-glue flag here

#if True                // @BUGBUG: Currently, this fails in some cases, but it will only lead to weird spacing in the output.
                        if (!model.ModelOptions.InlineFixes) // skip over inserted source/targets
#endif
                        Sanity.Requires(tokenList.Count == 0 || tokenList.Last().IsOfWordNature ||
                                        tokenList.Last().factors.glueRight == (glueLeft ? Factors.GLUE_RIGHT : Factors.GLUE_RIGHT_NOT),
                                        "Inconsistent glue factors across elided space??");

                        // create token with flags represented as factors
                        var token = tokenArray[i];
                        token.factors.glueLeft  = glueLeft  ? Factors.GLUE_LEFT  : Factors.GLUE_LEFT_NOT;
                        token.factors.glueRight = glueRight ? Factors.GLUE_RIGHT : Factors.GLUE_RIGHT_NOT;
                        tokenList.Add(token);
                        iLastNonElidedSpace = i;
                    }
                    // else don't update iLastNonElidedSpace
                }
                // "Word": set boundary factors
                else
                {
                    // boundary flags
                    // *Any* non-Word/Word transition is a considered a word beginning and/or end,
                    // while *all* Word/Word transitions are not considered such.
                    // Some Word-Word transitions may be linguistically considered word boundaries,
                    // such as a Kanji character followed by a Latin character. However, in our context,
                    // word beginning/end factors are concerned with space prediction, hence such transitions
                    // are still considered word-internal. If we didn't, then a space between them would be
                    // incorrectly implied.
                    // Another example are mixed letter/number strings such as "x64"; this is also *not*
                    // be marked as word beginning/end, for the same reason.
                    bool isWordBeg = !prevIsWord;
                    bool isWordEnd = !nextIsWord;

                    // create token with flags represented as factors
                    var token = tokenArray[i];
                    // if CJK then use special begin/end factors instead, since spacing behavior is different
                    bool isContinuousScript = token.IsContinuousScript;
                    bool diip = model.ModelOptions.DistinguishInitialAndInternalPieces;
                    if (isContinuousScript)      token.factors.csBeg   = isWordBeg ? Factors.CS_BEG   : Factors.CS_BEG_NOT;
                    else if (diip && !isWordBeg) token.factors.wordInt =                                Factors.WORD_INT;
                    else                         token.factors.wordBeg = isWordBeg ? Factors.WORD_BEG : Factors.WORD_BEG_NOT;
                    if (model.ModelOptions.RightWordGlue)
                    {
                        if (isContinuousScript) token.factors.csEnd   = isWordEnd ? Factors.CS_END   : Factors.CS_END_NOT;
                        else                    token.factors.wordEnd = isWordEnd ? Factors.WORD_END : Factors.WORD_END_NOT;
                    }
                    tokenList.Add(token);
                    iLastNonElidedSpace = i;
                }
            }
            Sanity.Requires(!model.ModelOptions.DistinguishInitialAndInternalPieces || tokenList.All(t => t.factors.wordBeg != Factors.WORD_BEG_NOT),
                            "WORD_BEG_NOT used with DistinguishInitialAndInternalPieces??");
            return tokenList.ToArray();
        }

        /// <summary>
        /// For InlineFixes mode, this method modifies the token sequence which has been
        /// annotated with inlineFix factors into the final syntax presented to Marian.
        /// This routine supports both InlineFixWithTags and not InlineFixWithTags.
        /// </summary>
        private IEnumerable<Token> FormatInlineFixes(IEnumerable<Token> tokens)
        {
            var prevFactor = Factors.INLINE_FIX_NONE;
            foreach (var token in tokens)
            {
                var thisFactor = token.factors.inlineFix ?? Factors.INLINE_FIX_NONE;
                if (model.ModelOptions.InlineFixUseTags)
                {
                    // Note: we explicitly allow zero-length sub-sequences here, so that we can support
                    // a hard phrase fix that does not even expose the source to self-attention, and
                    // empty outputs.
                    if (prevFactor != Factors.INLINE_FIX_WHAT && thisFactor == Factors.INLINE_FIX_WHAT)
                        yield return token.PseudoTokenAt(INLINE_FIX_SRC_ID_TAG, after: false);
                    else if (prevFactor != Factors.INLINE_FIX_WITH && thisFactor == Factors.INLINE_FIX_WITH)
                        yield return token.PseudoTokenAt(INLINE_FIX_TGT_ID_TAG, after: false);
                    else if (prevFactor != Factors.INLINE_FIX_NONE && thisFactor == Factors.INLINE_FIX_NONE)
                        yield return token.PseudoTokenAt(INLINE_FIX_END_ID_TAG, after: false);
                }
                var modifiedToken = token; // and remove the factor from any actual token
                // @TODO: To completely suppress the source word, add this:
                // if (modifiedToken.factors.inlineFix != Factors.INLINE_FIX_WHAT) { ...
                modifiedToken.factors.inlineFix = model.ModelOptions.InlineFixUseTags ? null : thisFactor;
                yield return modifiedToken;
                prevFactor = thisFactor;
            }
            if (model.ModelOptions.InlineFixUseTags && prevFactor != Factors.INLINE_FIX_NONE)
                yield return tokens.Last().PseudoTokenAt(INLINE_FIX_END_ID_TAG, after: true);
        }

        /// <summary>
        /// Replace index factors and unrepresentable characters into a form where the index
        /// value/character code is serialized into a sequence of digits. For example,
        /// If A|wb|cn was unencodable, the result would be {unk,w,c}|wb|cn <6> <5> <#>.
        /// (the factor types, 'w' and 'c' in this example, are encoded in the lemma name since
        /// each lemma has a unique set of factors).
        /// Likewise, {word}|...|index004 would become {word}|... <4> <#>.
        /// This encoding of is aimed at being easy to copy through. For unrepresentables
        /// (e.g. emojis or graphics symbols), the assumption is that that's the right way of "translating" them.
        /// </summary>
        /// This function must only be called if ModelOptions.SerializeIndicesAndUnrepresentables.
        private IEnumerable<Token> SerializeIndicesAndUnrepresentables(IEnumerable<Token> tokens)
        {
            Sanity.Requires(model.ModelOptions.SerializeIndicesAndUnrepresentables, "SerializeIndicesAndUnrepresentables called without corresponding option set");
            // loop over all tokens, and replace index or unrepresentable tokens by the serialized form
            foreach (var token in tokens)
            {
                bool hasIndex = token.factors.index != null;
                Sanity.Requires(hasIndex == (token.factors.spanClass != null), "Factor spanClass unexpectedly does not come with index factor");
                bool isUnrepresentable =
                    model.KnownLemmas != null &&
                    !token.IsClass &&    // optimization: all defined class tokens are known to be representable
                    token.Word.IsSingleCharConsideringSurrogatePairs() && // optimization: only single-character tokens can be unrepresentable
                    // @TODO: ^^ not maximally efficient; make a method on Token that does not make a copy first
                    !model.KnownLemmas.Contains(token.SubStringNormalizedForLemma(model.ModelOptions));
                Sanity.Requires(isUnrepresentable || model.KnownLemmas == null ||
                                (!token.Word.IsSingleCharConsideringSurrogatePairs() && model.SentencePieceModel == null) || // w/o SPM, this mechanism does not work. This happens only in testing, so it's OK
                                model.KnownLemmas.Contains(token.SubStringNormalizedForLemma(model.ModelOptions)),
                                $"Multi-character token '{token}' unexpectedly unrepresentable??"); // @TODO: This check is expensive, don't keep it around

                // unproblematic token
                if (!hasIndex && !isUnrepresentable)
                {
                    yield return token;
                    continue;
                }

                var headToken = token;
                int n; // the number to serialize
                if (isUnrepresentable) // unrepresentable gets serialized with {unk...} as head token
                {
                    Sanity.Requires(token.Word.IsSingleCharConsideringSurrogatePairs(), "Unrepresentable tokens can only be single characters");
                    n = (token.Length == 1 ? token.First() : char.ConvertToUtf32(token.At(0), token.At(1))); // serialize the Unicode code point
                    // since allowed factor set depends on the lemma, we include the factor set as well, e.g. "_c_wb" in the lemma name
                    string lemma = GetSerializedUnrepresentableLemma(token.factors);
                    // if a head token with this factor set has not been observed, we can only silently ignore the token
                    if (!model.KnownLemmas.Contains(lemma))
                        continue; // shhh! Pretend this did not happen
                    headToken = token.OverrideAsIf(lemma);
                }
                else // class with index gets serialized with original class token (e.g. {word}|...) as head token, with index factor removed
                {
                    n = token.ParseIndexFactor();
                    headToken.factors.index = null; // note that Validate() will now fail for this token, since 'spanClass' always requires 'index', so this form is only for serializing to Marian
                }
                yield return headToken;
                foreach (var c in n.ToString())
                    yield return headToken.PseudoTokenAt(Token.UNK_DIGITS[c - '0'], after: true);
                yield return headToken.PseudoTokenAt(Token.UNK_DIGITS_END, after: true);
            }
        }

        // Each head lemma for serialized unrepresentable characters must have a unique factor set.
        // Unrepresentable chars with different factor sets use different head lemmas.
        // We encode the factor-type names into the head-lemma name itself , e.g. "{unk,cn,wb}".
        private static string GetSerializedUnrepresentableLemma(Factors factors)
        {
            var factorSetString = string.Concat(from factorType in factors.Types select "," + factorType.ToString());
            var lemma = Token.UNK_LEMMA_PATTERN.Replace("*", factorSetString);
            return lemma;
        }

        // process the serialized form, e.g. {unk,...}|... <6> <5> <#> into a|... INVALID INVALID INVALID
        // The head token now has its desired final form, while the digit tokens are invalid,
        // and to be ignored by the subsequent users of the result.
        // It is called "in-place" because the invalid tokens are not deleted.
        // This is such that the token-index based alignments from MT remain valid.
        // This function must only be called if ModelOptions.SerializeIndicesAndUnrepresentables.
        private (IList<Token> tokens, IList<bool> tokenValidFlags) DeserializeIndicesAndUnrepresentablesInPlace(IList<Token> serializedTokens)
        {
            Sanity.Requires(model.ModelOptions.SerializeIndicesAndUnrepresentables, "DeserializeIndicesAndUnrepresentables called without corresponding option set");
            var tokens = serializedTokens; // we will return original if unmodified, but make a copy on first write
            var tokenValidFlags = new bool[tokens.Count]; // note: initialized to 'false'; gets set to 'true' token by token

            // loop over all tokens, replace index or unrepresentable tokens by the serialized form, and mark digits as INVALID
            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                var hasIndex = token.factors.spanClass != null; // spanClass factor implies index
                var isUnrepresentable = token.Word.StartsWith(Token.UNK_LEMMA_PREFIX);

                // unproblematic token
                if (!hasIndex && !isUnrepresentable)
                {
                    if (Array.IndexOf(Token.UNK_DIGITS, token) < 0 && token.Word != Token.UNK_DIGITS_END) // stray digits get discarded
                        tokenValidFlags[i] = true; // regular token, to be kept
                    continue;
                }

                // found a serialized one: parse the number
                int i0 = i; // location of head token
                int n = 0;
                bool isValid = false; // is this serialized sequence well-formed?
                for (i++; i < tokens.Count; i++)
                {
                    var lemma = tokens[i].Word;
                    if (lemma == Token.UNK_DIGITS_END)
                    {
                        isValid = true; // successful parse: sequence is valid
                        break;
                    }
                    int d = Array.IndexOf(Token.UNK_DIGITS, lemma); // map lemma string back to digit value
                    if (d == -1)
                        break;      // neither a digit nor the end symbol -> invalid
                    n = n * 10 + d; // number parsing like it's 1984
                }

                // transaltion may have generated an invalid sequence; the only thing we can do is silently drop the whole thing
                if (!isValid)
                    continue; // the entire sub-sequence will still have validFlags[] == false

                // emit the deserialized token
                var deserializedToken = token;
                if (hasIndex) // was an index
                {
                    if (n >= Factors.INDEX_FACTORS.Length)
                        continue; // out of range: silently drop
                    deserializedToken.factors.index = Factors.INDEX_FACTORS[n];
                    deserializedToken = deserializedToken.OverrideAsIf("");
                }
                else // was an unrepresentable
                {
                    try
                    {
                        deserializedToken = deserializedToken.OverrideAsIf(Char.ConvertFromUtf32(n)); // the unrepresentable character is back!
                    }
                    catch
                    {
                        // MT may have produced a digit sequence that does not constitute a valid Unicode code point
                        continue; // silently drop
                    }
                    // MT may have produced a character code that is inconsistent with the head token's factor set.
                    // E.g. a head token of word nature followed by a digit sequence that represents punctuation.
                    // In that case, we also silently drop the token
                    try
                    {
                        deserializedToken.Validate(model.ModelOptions);
                    }
                    catch (Exception)
                    {
                        continue; // yes: silently drop
                    }
                }
                if (tokens == serializedTokens)          // optimization: copy the array only if we actually change it (copy-on-write)
                    tokens = serializedTokens.ToArray(); // (if it is never changed, the original array will be kept and returned)
                tokens[i0] = deserializedToken;          // implant the fully reconstructed token
                tokenValidFlags[i0] = true;
            }
            return (tokens: tokens, tokenValidFlags: tokenValidFlags);
        }

        // helper to form token string for a sentence-level annotation, as passed to Marian, e.g. <SLA:target_language=ENU>
        static string ToSentenceAnnotationTokenString(string type, string value) => $"{Token.SLA_LEMMA_PREFIX}{type}={value}>";

        // helpers for parsing type and value from a sentence-level annotation string, e.g. <SLA:target_language=ENU> -> "target_language", "ENU"
        static bool IsSentenceAnnotationTokenString(string tokenString) => tokenString.StartsWith(Token.SLA_LEMMA_PREFIX);
        static bool IsSentenceAnnotationToken(Token token) => IsSentenceAnnotationTokenString(token.Word);
        static bool TryToSentenceAnnotationFromString(string tokenString, out string type, out string value)
        {
            if (!IsSentenceAnnotationTokenString(tokenString))
            {
                type = null;
                value = null;
                return false;
            }
            Sanity.Requires(tokenString.EndsWith(">"), $"Mal-formed sentence-annotation token {tokenString}, must have the form '{Token.SLA_LEMMA_PREFIX}KEY=VALUE>'");
            var token = tokenString.Substring(Token.SLA_LEMMA_PREFIX.Length, tokenString.Length - Token.SLA_LEMMA_PREFIX.Length - 1);
            var pair = token.Split('=');
            Sanity.Requires(pair.Length == 2, $"Mal-formed sentence-annotation token {tokenString}, must have the form '{Token.SLA_LEMMA_PREFIX}KEY=VALUE>'");
            type = pair[0];
            value = pair[1];
            return true;
        }
        // LINQ-callable version (LINQ 'let' does not allow for out vars)
        static internal (bool Found, string Type, string Value) TryToSentenceAnnotationFromString(string tokenString)
        {
            var found = TryToSentenceAnnotationFromString(tokenString, out var type, out var value);
            return (found, type, value);
        }
        // helper to collate a set of (type, value) sentence-annotation pairs into a dictionary type -> set of values
        static internal Dictionary<string, HashSet<string>> CollectSentenceAnnotations(FactoredSegmenterModel model, IEnumerable<(string Type, string Value)> pairs)
        {
            // initialize dict with all types
            Sanity.Requires(model.ModelOptions.SourceSentenceAnnotationTypeList.Any(), "Using sentence-level annotations requires model option SourceSentenceAnnotationTypes to be non-empty");
            var sourceSentenceAnnotationsDict = model.ModelOptions.SourceSentenceAnnotationTypeList.ToDictionary(key => key, _ => new HashSet<string>());
            // fill in the observed values
            foreach (var pair in pairs)
            {
                var found = sourceSentenceAnnotationsDict.TryGetValue(pair.Type, out var valueSet);
                Sanity.Requires(found, $"Unknown type '{pair.Type}' in sentence-level annotation '{pair.Type}={pair.Value}'. All types must be listed in model option SourceSentenceAnnotationTypes");
                valueSet.Add(pair.Value);
            }
            return sourceSentenceAnnotationsDict;
        }

        private (IEnumerable<Token> Tokens, Dictionary<int, string> DecodeAsTable, Token wholeLineToken) 
            TokenizeAndFactorize(ref string line, List<AnnotatedSpan> annotatedSpans = null, int? seed = null)
        {
            // - break string into tokens according to FS rules
            //    - does not add factors except for annotated classes
            //    - does not change the string except for AnnotatedSpans replacements
            var (tokens, decodeAsTable, wholeLineToken) = Pretokenize(ref line, annotatedSpans, seed);

            // - SentencePiece tokenization of lower-case FS tokens
            //    - except for single-char strings, which include space and the accidental ctrl char
            //    - add word-internal glue factors arising from SPM breaking words up
            //    - do not propagate the input's init-caps to all but first token
            //    - if we use a word-initial prefix, that may get separated into a zero-length token--must be handled correctly in subsequent code
            if (spmCoder != null)
                tokens = from tf in TokensWithLemmaWordBegPrefix(tokens)
                         let token              = tf.token
                         let lemmaWordBegPrefix = tf.lemmaWordBegPrefix
                         from splitToken in token.SplitToken(spmCoder.Split(token.SubStringNormalizedForSPM(lemmaWordBegPrefix), adjustForWordBegPrefix: lemmaWordBegPrefix))
                         select splitToken;

            // - apply FactoredSegmenter transformations
            //    - further splitting
            //    - collapse spacing information in punctuation and word-boundary info
            tokens = Factorize(tokens);

            return (tokens, decodeAsTable, wholeLineToken);
        }

        /// <summary>
        /// Encode a line of raw plain text into the form needed by Marian NMT training.
        /// This is the main function of FactoredSegmenter.
        /// </summary>
        /// <param name="line">Raw plain-text source line</param>
        /// <param name="annotatedSpans">Annotations. May be null, and each span item may be null as well.</param>
        /// <param name="seed">Random seed for randomizing id of annotations that map to class tokens. Required if such annotations are passed.</param>
        /// <returns></returns>
        public override IEncoded Encode(string line,
            List<AnnotatedSpan> annotatedSpans = null,
            Dictionary<string, string> sourceSentenceAnnotations = null,
            int? seed = null)
        {
            // break string into tokens using FS rules, and then further according to SentenceP
            var (tokens, decodeAsTable, wholeLineToken) = TokenizeAndFactorize(ref line, annotatedSpans, seed);

            // At this point, the tokens have been split and annotated with factors. The main work is done.
            // After this point, the token sequence undergoes further transformations
            // that are encoding-related.

            // some sanity checks
            foreach (var token in tokens)
                token.Validate(model.ModelOptions);

            // - encode inline fixes in tag format
            if (model.ModelOptions.InlineFixes)
                tokens = FormatInlineFixes(tokens);

            // - serialize index factors and unrepresentable characters into sequences of digits
            if (model.ModelOptions.SerializeIndicesAndUnrepresentables)
                tokens = SerializeIndicesAndUnrepresentables(tokens);

            // - insert sentence-level annotations
            if (sourceSentenceAnnotations != null)
            {
                foreach (var type in model.ModelOptions.SourceSentenceAnnotationTypeList)
                    Sanity.Requires(sourceSentenceAnnotations.ContainsKey(type), $"Incomplete sentence annotation. No value provided for type '{type}'");
                var annotationTokens = from type in model.ModelOptions.SourceSentenceAnnotationTypeList
                                       let value = sourceSentenceAnnotations[type]
                                       let tokenString = ToSentenceAnnotationTokenString(type, value)
                                       select wholeLineToken.Narrow(0, 0).OverrideAsIf(tokenString); // align with the entire sentence
                Sanity.Requires(sourceSentenceAnnotations.Keys.Count() == annotationTokens.Count(), $"Sentence annotation must contain exactly one entry for each type in mode option SourceSentenceAnnotationTypes");
                tokens = annotationTokens.Concat(tokens);
            }

            // - serialize to line encoding  --done in Encoded structure
            var decoderPackage = new DecoderPackage
            {
                DecodeAsTable = decodeAsTable
                //Encoded = encodedLine // circular reference, implanted below
            };
            var encodedLine = new Encoded(line, sourceSentenceAnnotations, tokens, model.ModelOptions, decoderPackage: decoderPackage);
            decoderPackage.Encoded = encodedLine;

            // some sanity checks
            if (IsTrainingScenario && (checkCounter++ % checkEvery == 0 || checkCounter <= 30))
                CheckEncoding(encodedLine, checkCounter);

            return encodedLine;
        }

        /// <summary>
        /// Wrapper around fsm.Encode() that logs failure cases.
        /// Note: Encode() is used at runtime and must therefore not log inputs due to privacy/PI.
        /// This wrapper function logs erroneous input for diagnostics, and may therefore only be called in training.
        /// </summary>
        IEncoded EncodeWithLogging(string line)
        {
            try
            {
                return Encode(line);
            }
            catch (Exception)
            {
                Logger.WriteLine($"Encode() failed with this input: {line}");
                throw;
            }
        }

        // Workaround for Bug #101419 "Training of allcaps factors is inconsistent".
        // Changes all single uppercase letters that are part of an an all-caps sequence to CAP_ALL.
        // Called if FactoredSegmenterModelOptions.UseContextDependentSingleLetterCapitalizationFactors;
        // see the comment for this flag for more info.
        private static void MakeContextDependentSingleLetterCapitalizationFactors(Token[] tokenArray)
        {
            // First reconstruct word boundaries, that is, a consecutive sequence of pieces of Word nature, and their cap factors.
            var wordRanges = new List<(int begin, int end, Factor cap)>(); // (begin, end, caps factor) token ranges of bicameral words and their capitalization (see comment below)
            int inWord = -1; // -1: no, 0: inside non-bicameral word such as CJK or digit sequence, 1: inside bicameral word
            int rangeBegin = -1;
            Factor rangeCap = null; // CAP_ALL: surely all-caps; CAP_NONE: surely not all-caps; CAP_INITIAL: consists only of single uppercase letter hence not decidable here
            for (var i = 0; i < tokenArray.Length; i++)
            {
                var cap    = tokenArray[i].factors.cap;
                int isWord = !tokenArray[i].IsOfWordNature  ?     -1 :  // Punc. For DistinguishInitialAndInternalPieces, this can also be the _, i.e. it does not become part of the word, and just gets seen-through
                             cap != null                    ?      1 :  // Word and bicameral
                             tokenArray[i].IsAllCombiners() ? inWord :  // all-combiner: keep in same word as previous token
                             /*otherwise*/                         0;   // Word without capitalization, e.g. CJKT
                var isUppercaseLetter = (cap == Factors.CAP_INITIAL && tokenArray[i].Length == 1);
                var hasLowercase      = cap == Factors.CAP_NONE || (cap == Factors.CAP_INITIAL && !isUppercaseLetter);
                Sanity.Requires(isWord != -1 || cap == null, "Punctuation with capitalization factors??");
                if (isWord == inWord) // inside consecutive stretch: update the cap factor
                {
                    if (rangeCap != null &&            // null here means we are in Punc or non-bicameral Word
                        cap      != null &&            // null here indicates an all-combiner while inside bicameral Word (if not, rangeCap would be null)
                        rangeCap != Factors.CAP_NONE)  // if we already found a CAP_NONE, this word is locked in to not be all-caps
                    {
                        if (hasLowercase) // found a lower-case: that seals it
                            rangeCap = Factors.CAP_NONE; // at least one lower-case character found: word is confirmed to be not all-caps
                        else if (isUppercaseLetter && rangeCap == Factors.CAP_INITIAL)
                            rangeCap = Factors.CAP_ALL;  // two single uppercase letters in a row qualifies as all-caps-so-far
                        else if (cap == Factors.CAP_ALL) // we only get here if word so far only contains CAP_ALL or single uppercase letters
                            rangeCap = Factors.CAP_ALL;  // -> that's an all-caps-so-far
                        // Note: If word consists of a single uppercase letter, the resulting factor will remain CAP_INITIAL.
                    }
                }
                else // hit a boundary
                {
                    if (inWord == 1) // close out the current one if bicameral
                    {
                        wordRanges.Add((rangeBegin, i, rangeCap));
                        rangeBegin = -1; // (not strictly needed, but useful for debugging)
                        rangeCap = null;
                    }
                    if (isWord != -1) // entering a word
                    {
                        rangeBegin = i;
                        rangeCap = isWord != 1  ? null :             // no cap factor if not bicameral
                                   hasLowercase ? Factors.CAP_NONE : // has lower
                                   /*otherwise*/  cap;               // keep CAP_ALL or CAP_INITIAL
                    }
                }
                inWord = isWord;
            }
            if (inWord == 1) // close potential final unterminated bicameral word
                wordRanges.Add((rangeBegin, tokenArray.Length, rangeCap));
            // wordRanges now contains all bicameral words.
            // The wordRanges cap values now are one of these:
            //  - CAP_INITIAL: word is confirmed to not be all-caps (found at least one lower-case letter)
            //  - CAP_ALL:     word is confirmed all-caps (found no lower-case, at least one CAP_ALL or 2 consecutive uppercase letters, and possibly more single uppercase letters)
            //  - CAP_INITIAL: word consists of 1 uppercase letter
            //  - quartum non datur

            // Note the special case ABCDef, which may get split e.g. as AB|ca C|ci Def|ci.
            // According to this algorithm, this would not qualify.
            // @TODO: should we see-through non-cap words or not? E.g. in "I NEED 100 DOLLARS", "I" would not qualify.

            // Single-uppercase-letter words are considered all-caps if they occur next to or between two bicameral words
            // that are known to be all-caps. At sentence boundary, one all-caps neighbor is sufficient.
            // Non-bicameral words and punctuation are not counted, besides delimiting bicameral words.
            // (Being next to a single all-caps word is too risky, e.g. "The PC I bought").
            // @TODO: expand to two neighbor words, or at least one on either side (one more loop)
            for (var j = 1; j < wordRanges.Count(); j++)
            {
                if ((j - 2 < 0 || wordRanges[j - 2].cap == Factors.CAP_ALL) &&  // at sentence boundary, a single all-caps is sufficient
                    wordRanges[j - 1].cap == Factors.CAP_ALL &&
                    wordRanges[j].cap == Factors.CAP_INITIAL)
                    wordRanges[j] = (wordRanges[j].begin, wordRanges[j].end, Factors.CAP_ALL);
            }
            for (var j = wordRanges.Count() - 2; j >= 0; j--)
            {
                if ((j + 2 >= wordRanges.Count() || wordRanges[j + 2].cap == Factors.CAP_ALL) &&
                    wordRanges[j + 1].cap == Factors.CAP_ALL &&
                    wordRanges[j].cap == Factors.CAP_INITIAL)
                    wordRanges[j] = (wordRanges[j].begin, wordRanges[j].end, Factors.CAP_ALL);
            }
            for (var j = 1; j < wordRanges.Count() - 1; j++)
            {
                if (wordRanges[j - 1].cap == Factors.CAP_ALL &&
                    wordRanges[j    ].cap == Factors.CAP_INITIAL &&
                    wordRanges[j + 1].cap == Factors.CAP_ALL)
                    wordRanges[j] = (wordRanges[j].begin, wordRanges[j].end, Factors.CAP_ALL);
            }

            // Single uppercase letters are considered all-caps if they occur inside a known all-caps word.
            foreach (var wordRange in wordRanges)
            {
                for (var i = wordRange.begin; i < wordRange.end; i++)
                {
                    if (wordRange.cap == Factors.CAP_ALL && tokenArray[i].factors.cap == Factors.CAP_INITIAL)
                    {
                        Sanity.Requires(tokenArray[i].IsOfWordNature && tokenArray[i].Length == 1, "Incorrectly attempting to all-caps a token with lowercase letters??");
                        tokenArray[i].factors.cap = Factors.CAP_ALL;
                    }
                }
            }
        }

        // -------------------------------------------------------------------
        // decoding
        // -------------------------------------------------------------------

        /// <summary>
        /// Return value of the Decode() function.
        /// </summary>
        public class Decoded : IDecoded
        {
            public override DecodedSegment[] Tokens { get; }

            internal Decoded(DecodedSegment[] tokens)
            {
                Tokens = tokens;
            }
        }

        /// <summary>
        /// phrasefix is special. i.e. we always want to correctly have a phrasefix replaced.
        /// sometimes, the model drops the phrasefix. In that case, we do have to find for those cases and put them back in tgt
        /// we put this in the correct location using the alignment and modify the same.
        /// alignmentFromMT is modifiedInPlace
        /// </summary>
        /// <param name="hypTokens">decoded tokens before inserting missing phrasefixes</param>
        /// <param name="hypTokenValidFlags">bool per outputToken, indicating whether it should be kept (true) or cleaned up (false) later</param>
        /// <param name="alignmentFromMT">token to token alignments output by the MT model. Will be modified in place if necessary. 
        /// @TODO: this should be part of the return type rather than modifying in place. (work item #109161)</param>
        /// <param name="sourceTokens">source side tokens - any phrasefix tokens here must appear in the output</param>
        /// <returns>copies of outputTokens and tokenValidFlags with missing phrasefixes (and corresponding valid flag) inserted where necessary.</returns>
        private (IList<Token> ResultingOutputTokens, IList<bool> ResultingTokenValidFlags) InsertMissingPhrasefixes(
            IList<Token>       hypTokens, 
            IList<bool>        hypTokenValidFlags, 
            ref Alignment      alignmentFromMT, 
            IEnumerable<Token> sourceTokens)
        {
            // find any missing phrasefix and insert it here.
            // you can search for classphrasefix tokens in src missing in tgt and add them to appropriate location in tgt
            // when it works, this is what we get:
            //  src - {word}|cn|wb|classphrasefix|index032
            //  tgt - {word}|cn|classphrasefix|index032|wb
            var allSourcePhraseFixes  = FindAllPhrasefixTokens(sourceTokens);
            var allHypPhraseFixes     = FindAllPhrasefixTokens(hypTokens);

            // it's possible index on a hyp phrasefix token can be null, if the serialized index tokens after the phrasefix head token are malformed.
            KeyValuePair<int, Token>[] missingPhrasefixTokens = 
                allSourcePhraseFixes.Where(inputPF =>
                    inputPF.Value.factors.index?.Serialized != null && 
                    !allHypPhraseFixes.Any(outputPF => outputPF.Value.factors.index?.Serialized == inputPF.Value.factors.index.Serialized))
                .ToArray();

            if (missingPhrasefixTokens.Length == 0)
                return (hypTokens, hypTokenValidFlags);

            // need an explicit List for insertion, since IList may be an array
            List<Token> workingOutputTokens    = hypTokens.ToList();
            List<bool>  workingTokenValidFlags = hypTokenValidFlags?.ToList();

            // if absent in the output insert it in the correct place according to the alignment information.
            foreach (var missingInputPhrasefix in missingPhrasefixTokens)
                InsertDroppedToken(missingInputPhrasefix.Key, missingInputPhrasefix.Value, workingOutputTokens, workingTokenValidFlags, ref alignmentFromMT);

            return (workingOutputTokens, workingTokenValidFlags);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void InsertDroppedToken(int srcIndex, Token missingPhrasefix, List<Token> hypTokens, List<bool> hypTokenValidFlags, ref Alignment alignment)
        {
            // what's a good place to insert this token?
            int targetIndex = alignment.GetTargetIndexToInsert(srcIndex);

            if (hypTokens.Count < targetIndex || targetIndex == -1)
                targetIndex = hypTokens.Count;

            // assuming this is a phrasefix situation, hence take the first one and apply the translations.
            hypTokens.Insert(targetIndex, missingPhrasefix);
            if (hypTokenValidFlags != null)
                hypTokenValidFlags.Insert(targetIndex, true);

            // account for this additional link in the alignment info.
            alignment = alignment.InsertMissingTarget(srcIndex, targetIndex);
        }

        private Dictionary<int, Token> FindAllPhrasefixTokens(IEnumerable<Token> tokens)
        {
            Dictionary<int, Token> phrasefixes = new Dictionary<int, Token>(); // [source token index] -> token that is a phrase fix.
            if (tokens != null)
            {
                int i = 0;
                foreach(var token in tokens)
                {
                    if (token.factors.spanClass == Factors.CLASS_FACTORS[AnnotatedSpanClassType.PhraseFix])
                        phrasefixes.Add(i, tokens.ElementAt(i));
                    i++;
                }
            }
            return phrasefixes;
        }

        /// <summary>
        /// Decode a sequence of tokens in serialized Marian-NMT form, as generated by the in-memory Marian decoder.
        /// The resulting number of surface-form segments is *not* the same as tokenStrings.Length due to the added segments.
        /// </summary>
        public override IDecoded Decode(IEnumerable<string> tokenStrings, Alignment alignmentFromMT, IDecoderPackage iDecoderPackage = null)
        {
            var decoderPackage = iDecoderPackage as DecoderPackage;

            // convert tokens in string form into internal data structure (parse factor syntax)
            IList<Token> tokens = (from tokenString in tokenStrings select Token.Deserialize(tokenString, model.ModelOptions)).ToList();

            // strip sentence annotations, in case of testing round-tripping or spurious occurences from translator
            if (model.ModelOptions.SourceSentenceAnnotationTypeList.Any())
                tokens = tokens.Where(token => !IsSentenceAnnotationToken(token)).ToList();

            // deserialize indices and unrepresentables
            // This will restore the index factor and/or unrepresentable char string in the
            // respective head token of the serialized sequence, while changing the remaining
            // tokens to invalid tokens (e.g. {unk,...}|... <6> <5> <#> --> a|... INVALID INVALID INVALID).
            // This way, alignment information from Marian is kept intact. We will eliminate
            // those entries further down in this function, right before decoding into surface form.
            // NOTE: in some (newer) models, phrase fix tokens are also encoded using serialized sequences like this.
            IList<bool> tokenValidFlags = null; // invalidFlags[i] = true means tokens[i] is INVALID
            if (model.ModelOptions.SerializeIndicesAndUnrepresentables)
                (tokens, tokenValidFlags) = DeserializeIndicesAndUnrepresentablesInPlace(tokens);

            // hack to force unseen phrase fixes to show up at least somewhere
            if (decoderPackage != null)
            {
                IList<Token> encodedTokens = decoderPackage?.Encoded.encodedTokens;
                if (model.ModelOptions.SerializeIndicesAndUnrepresentables)
                    encodedTokens = DeserializeIndicesAndUnrepresentablesInPlace(encodedTokens).tokens;

                // will update 'tokens' in-place and replace the 'alignmentFromMT' pointer
                // it may make more sense to do this operation using alignment links below, but for now, the algorithm requires legacy alignments
                (tokens, tokenValidFlags) = InsertMissingPhrasefixes(tokens, tokenValidFlags, ref alignmentFromMT, encodedTokens); 
            }

            Sanity.Requires(tokenValidFlags == null || tokens.Count == tokenValidFlags.Count, "tokenValidFlags must have same length as tokens");

            var alignmentLinks = alignmentFromMT?.Links;
            // create associated alignment-link arrays
            // For each decoded target token, we create a list of corresponding source-character ranges (with link confidence).
            List<DecodedSegment.SourceLink>[] tokenSourceAlignmentLinks;
            if (alignmentLinks != null && decoderPackage != null)
            {
                var originalSourceTextSegments = decoderPackage.Encoded.OriginalSourceTextSegments;
                tokenSourceAlignmentLinks = new List<DecodedSegment.SourceLink>[tokens.Count]; // [tgt index] -> alignment links to source char ranges
                foreach (var alignmentLink in alignmentLinks)
                {
                    // convert alignmentLink into a SourceLink, and collect those attached to their respective target positions
                    int s = alignmentLink.SourceIndex;
                    int t = alignmentLink.TargetIndex;
                    Sanity.Requires(s >= 0 && s < originalSourceTextSegments.Length, $"Alignment link source index {s} is out of bounds");
                    Sanity.Requires(t >= 0 && t < tokens.Count,          $"Alignment link target index {t} is out of bounds");
                    if (tokenSourceAlignmentLinks[t] == null) // (List is lazy; null means no source links)
                        tokenSourceAlignmentLinks[t] = new List<DecodedSegment.SourceLink>();
                    // remember link for target position t into source position s, expressed as source character range
                    tokenSourceAlignmentLinks[t].Add(new DecodedSegment.SourceLink
                    {
                        SourceSegment = originalSourceTextSegments[s],
                        Confidence = alignmentLink.Confidence
                    });
                }
                // normalize order
                // @TODO: There can be multiple identical source links for a target position. Should we merge them?
                foreach (var linkSet in tokenSourceAlignmentLinks)
                    linkSet?.Sort((a, b) => a.SourceSegment.StartIndex.CompareTo(b.SourceSegment.StartIndex));
                // @TODO: also sort by length ^^ to make it stable. Or use OrderBy(), which is a stable sort
            }
            else
                tokenSourceAlignmentLinks = null;

            // if index/unrepresentable deserialization, then we must delete digit tokens (as controlled by tokenValidFlags)
            if (tokenValidFlags != null)
            {
                // delete all items in both tokens and tokenSourceAlignmentLinks that have tokenValidFlags[.] = false
                tokens = tokens.Where((Token _, int i) => tokenValidFlags[i]).ToList();
                if (tokenSourceAlignmentLinks != null)
                    tokenSourceAlignmentLinks = tokenSourceAlignmentLinks.Where((List<DecodedSegment.SourceLink> _, int i) => tokenValidFlags[i]).ToArray();
            }

            // validate
            foreach (var token in tokens)
                token.Validate(model.ModelOptions);

            // decode the tokens into surface forms with source-alignment info
            return new Decoded(DecodeIntoConsecutiveSegments(tokens.ToArray(),
                               tokenSourceAlignmentLinks,
                               decoderPackage?.DecodeAsTable));
        }

        // Helper to create surface-form segments with word-boundary information, as well as space tokens, from factored tokens.
        // This does two things:
        //  - internal Token (plus alignment info) is converted into the external SegmenterToken format
        //  - space is inserted as additional tokens
        // The number of output items is *not* the same as tokens.Length due to the added spaces.
        // Which is fine because the caller should not be concerned with the internal token structure.
        internal static DecodedSegment[] DecodeIntoConsecutiveSegments(IList<Token> tokens,
                                                                       IList<List<DecodedSegment.SourceLink>> tokenSourceAlignmentLinks,
                                                                       Dictionary<int, string> decodeAsTable)
        {
            // we form an extended version of the input which has spaces inserted, based on glue factors
            var segments      = new List<string>();                          // decoded surface-form segments
            var srcLinks      = new List<List<DecodedSegment.SourceLink>>(); // source char ranges that the segments originated from
            var isStart       = new List<bool>();                            // is segment[i] is a word start?
            var isCSBreak     = new List<bool>();                            // does segment[i] have a space before it for non-continous languages?
            var isForceDecode = new List<bool>();                            // is segment[i] a phrasefix output?

            // expand each token from MT into 1 segment or 2 if a space is inserted
            var prevHadGlueRight = true;       // no implied space at sentence start (or end)
            var prevNotAWord = false;          // force a boundary if first token is punctuation
            var prevWasContinuousScript = false;  // force a boundary before AND after each continuous script token
            var isFirst = true;
            for (int t = 0; t < tokens.Count; t++)
            {
                var token = tokens[t];
                var links = tokenSourceAlignmentLinks?[t]; // source range(s) that this token came from
                var surfaceForm = token.SurfaceForm(decodeAsTable);
                if (surfaceForm == "" && token.factors.index != null && decodeAsTable != null) // class with index that is not found in decodeAsTable; drop
                    continue;
                var hasGlueRight = token.factors.glueRight == Factors.GLUE_RIGHT || token.factors.wordEnd == Factors.WORD_END_NOT || token.factors.csEnd == Factors.CS_END_NOT;
                var hasGlueLeft  = token.factors.glueLeft  == Factors.GLUE_LEFT  || token.factors.wordBeg == Factors.WORD_BEG_NOT || token.factors.csBeg == Factors.CS_BEG_NOT || token.factors.wordInt == Factors.WORD_INT;
                var notAWord = token.factors.glueRight != null || token.factors.glueLeft != null; // 'word' here means follows word or CS spacing rules
                var isContinuousScript = token.factors.csBeg == Factors.CS_BEG || token.factors.csBeg == Factors.CS_BEG_NOT;
                var insertSpaceBefore = !prevHadGlueRight && !hasGlueLeft;
                if (insertSpaceBefore)
                {
                    segments.Add(" ");
                    isForceDecode.Add(false);
                    isStart.Add(true);
                    isCSBreak.Add(true);
                    srcLinks.Add(null); // reconstructed space has no originating source range
                }
                segments.Add(surfaceForm);
                isForceDecode.Add(token.IsClass); // did this have a replacement string? (was it a phrase fix)

                isStart.Add(insertSpaceBefore ||                          // space implies a boundary
                            (notAWord && !prevNotAWord) ||                // transition from word into punctuation
                            token.factors.wordBeg == Factors.WORD_BEG ||  // transition into a word...
                            isContinuousScript ||                         // beginning and end of any continuous-script segment is considered a word-boundary
                            prevWasContinuousScript);    

                srcLinks.Add(links);

                // This is used to determine valid points to insert tags and boundaries of spans for character alignments
                // Scenario. Say there's a valid number like 32,456.78
                // Now, when inserting tags, we want to know where to insert them. We shouldn't try to insert 
                // tags _inside_ a number. Hence, we want the definition of start of the word to be
                // cognizant of that fact. Thus for languages that have spaces we want to rely on
                // whether the word is glued to the left to indicate the 'word start' flag.
                isCSBreak.Add(isFirst                               ||  // first token is always a spacing start
                              insertSpaceBefore                     ||  // space implies a boundary
                              isContinuousScript ||                     // beginning and end of any continuous-script segmnet is considered a word-boundary
                              prevWasContinuousScript);

                prevHadGlueRight = hasGlueRight;
                prevNotAWord = notAWord;
                prevWasContinuousScript = isContinuousScript;
                isFirst = false;
            }
            isCSBreak.Add(true);
            isStart.Add(true); // (one more boundary to represent the end of the sentence)

            // return as array of SegmenterTokens
            var res = from i in Enumerable.Range(0, segments.Count)
                      select new DecodedSegment(segments[i], isStart[i], isStart[i + 1], srcLinks[i], isForceDecode[i], isCSBreak[i], isCSBreak[i + 1]);
            return res.ToArray();
        }

        /// <summary>
        /// Finds CharacterSpans for utf32 characters in an input string that cannot be encoded by this model.
        /// Uses KnownLemmas to determine if something can be encoded or not. This is needed so that we can determine
        /// which tokens contain characters that can't be encoded by a given fsm, so that they can be passed through
        /// without sending to the translation model.
        /// </summary>
        /// <param name="line">An input string to search for unrepresentable characters</param>
        public override IEnumerable<(int StartIndex, int Length)> FindUnrepresentableSpans(string line)
        {
            if (model.KnownLemmas == null)
                yield break;

            // For efficiency, first check if there are any characters or surrogate pairs) in the input that don't have lemmas. 
            // if there are none, we are guaranteed to be able to encode the entire sentence, so no need for the more expensive check below.
            if (Unicode.EnumerateUtf32CodePointsAsStrings(line)
                       .All(utf32Char => string.IsNullOrWhiteSpace(utf32Char) ||
                                         model.KnownLemmas.Contains((new Token(utf32Char)).SubStringNormalizedForLemma(model.ModelOptions))))
            {
                yield break;
            }

            (IEnumerable<Token> tokens, _, _) = TokenizeAndFactorize(ref line);

            foreach (var token in tokens)
            {
                if (token.IsSpace)
                    continue;

                string normedLemma = token.SubStringNormalizedForLemma(model.ModelOptions);
                if (!model.KnownLemmas.Contains(normedLemma))
                    yield return (token.OrigRange.StartIndex, token.OrigRange.Length);
            }
        }

        // -------------------------------------------------------------------
        // test support
        // -------------------------------------------------------------------

        /// <summary>
        /// Factory to create a FactoredSegmentModel for tests.
        /// Normally, this is a fake (SPM-less) model.
        /// For debugging with a specific model, a path to a model can be passed.
        /// Some options can be passed.
        /// </summary>
        /// <returns></returns>
        public static FactoredSegmenterCoder CreateForTest(string path = null,
                                                           bool singleLetterCaseFactors = false,
                                                           bool distinguishInitialAndInternalPieces = false,
                                                           bool serializeIndicesAndUnrepresentables = false,
                                                           bool inlineFixes = false,
                                                           bool rightWordGlue = false,
                                                           string[] sourceSentenceAnnotationTypes = null)
        {
            if (path == null)
                return new FactoredSegmenterCoder(new FactoredSegmenterModel(new FactoredSegmenterModelOptions
                {
                    SingleLetterCaseFactors = singleLetterCaseFactors,
                    DistinguishInitialAndInternalPieces = distinguishInitialAndInternalPieces,
                    SerializeIndicesAndUnrepresentables = serializeIndicesAndUnrepresentables,
                    InlineFixes = inlineFixes,
                    RightWordGlue = rightWordGlue,
                    UseSentencePiece = false,
                    SourceSentenceAnnotationTypes = sourceSentenceAnnotationTypes == null ? null : String.Join(";", sourceSentenceAnnotationTypes)
                    // @TODO: use a ?? expression inside Join()
                }));
            else
                return new FactoredSegmenterCoder(FactoredSegmenterModel.Load(path));
        }

        /// <summary>
        /// A test helper function that verifies that the encoding of a line of raw plain text is indeed
        /// reversible. We run this check over a subset of all encoded lines.
        /// There are a few cases where encoding is presently not reversible. Therefore, we
        /// don't terminate on error, but merely log.
        /// </summary>
        public bool CheckEncoding(IEncoded iEncoded, int lineNo) // verify
        {
            var encoded = iEncoded as Encoded;
            var s = encoded.OriginalSourceText;
            var e = encoded.ToString();
            var d = Decode(e, encoded.DecoderPackage).ToString();
            bool ok = d == s;
            if (!ok)
                // note: do not fail, let's collect the cases where it happens here:
                //  - for Turkish and Azerbaijani 'i', where capitalization does not work correctly with InvariantCulture
                //  -   src: иみヘいΤ程ㄎ匡。 -->   dec: иみヘいτ程ㄎ匡。 The T is not an ASCII letter. GREEK TAU?
                Logger.WriteLine($"Round trip fidelity error encoding the {lineNo}-th line:\n  fsm: {e}\n  src: {s}\n  dec: {d}");
            return ok;
        }

        /// <summary>
        /// A test helper that encodes a line of raw text and verifies that it can be reversibly decoded.
        /// Optionally, it can verify that the number of segments before SentencePiece is as expected.
        /// </summary>
        public bool Test(string s, List<AnnotatedSpan> annotatedSpans = null, Dictionary<string, string> sourceSentenceAnnotations = null, int? numSegmentsBeforeSPM = null)
        {
            var encoded = Encode(s, annotatedSpans: annotatedSpans, sourceSentenceAnnotations: sourceSentenceAnnotations);
            var ok = CheckEncoding(encoded, 0);
            if (numSegmentsBeforeSPM != null)
            {
                var tokens = Pretokenize(ref s).Tokens.ToArray();
                ok = tokens.Length == numSegmentsBeforeSPM;
                if (!ok) // @TODO: We can actually terminate on error here
                    Logger.WriteLine($"Unexpected number of tokens {tokens.Length} expected {numSegmentsBeforeSPM}:\n  fsm: {" ".JoinItems(tokens)}\n  src: {s}");
            }
            return ok;
        }

        /// <summary>
        /// Helper function for testing, remove digit tokens for a serialized index. This allows us to test 
        /// </summary>
        public IEnumerable<string> StripDigitTokensForTest(IEnumerable<string> tokensToStrip)
        {
            Sanity.Requires(this.model.ModelOptions.SerializeIndicesAndUnrepresentables, "Stripping digit tokens for testing should only be done with a model that serializes indices.");

            foreach (var tok in tokensToStrip)
            {
                var deserializedTok = Token.Deserialize(tok, this.model.ModelOptions);
                if (deserializedTok.IsSpecialTokenWithoutFactors)
                    continue;

                yield return tok;
            }
        }
    }
}
