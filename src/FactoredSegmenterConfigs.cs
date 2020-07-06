// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Xml.Serialization;
using Common.Utils;

namespace Microsoft.MT.Common.Tokenization
{
    /// <summary>
    /// Configurable options for FactoredSegmenter models.
    /// All options that are kept inside the model file go here.
    /// </summary>
    public class FactoredSegmenterModelOptions
    {
        /// <summary>
        /// if false, do not emit |we nor |ce factors
        /// </summary>
        public bool RightWordGlue { get; set; } = false;

        /// <summary>
        /// if true, word-internal and word-initial pieces use distinct lemmas
        /// Without this, as piece xyz can exist in at least four forms, which in original
        /// SentencePiece notation would be written as xyz, Xyz, _xyz, and _Xyz.
        /// The latter three are all word boundaries, while the first is word-internal.
        /// I.e. two fundamentally different units are mapped onto the same piece.
        /// With this flag set, the latter three will use a different symbol.
        /// ...This is experimental, and not yet confirmed to help.
        /// </summary>
        public bool DistinguishInitialAndInternalPieces { get; set; } = false;

        public bool SplitHan { get; set; } = false;

        /// <summary>
        /// separate case factors for single letters
        /// For single letters, it is not clear whether to use |ca or |ci.
        /// With this option, we use a completely different factor |scu or |scl for single-letter words.
        /// This seems to quite robustly improve capitalization for English "I" and "U.S." for example.
        /// </summary>
        public bool SingleLetterCaseFactors { get; set; } = false;

        /// <summary>
        /// serialize phrase-fix indices and unrepresentable characters
        /// With this option, the index factor is no longer an additive factor,
        /// but instead is represented as a sequence of digits. This frees bits in Marian for other factors.
        /// Likewise, unrepresentable characters (=single characters not found in the
        /// SentencePiece vocabulary) are also serialized as their Unicode in digit form.
        /// This allows for any character to be represented (and hopefully translated by at least copying it through).
        /// ...This is experimental.
        public bool SerializeIndicesAndUnrepresentables { get; set; } = false;

        /// <summary>
        /// If true, phrase fixes are encoded by including them in the source.
        /// ...This is experimental.
        /// ...This is ongoing work. The following will be addressed once we know whether this works at all:
        ///  - no correct escaping of our internal delimiter chars if they occur in real text
        ///  - delimiter chars should be encoded in the form as XML tags, and have no glue factors
        ///  - glue/boundary-factor determination must correctly see through the delimited ranges
        ///  - currently if the decoder decides to output the delimiter chars, they will not be removed
        ///  - delimiter chars should be excluded from shortlists (as should sentence-start)
        /// </summary>
        public bool InlineFixes { get; set; } = false;

        /// <summary>
        /// If true, use start/middle/end tags (which is known to not work well).
        /// If false, then use INLINE_FIX_TYPE factors for the inline-fix tokens.
        /// Only used if InlineFixes == true.
        /// </summary>
        public bool InlineFixUseTags { get; set; } = false;

        /// <summary>
        /// Enables context-dependent capitalization factors for single letters.
        /// Workaround for Bug #101419 "Training of allcaps factors is inconsistent".
        /// The Marian all-caps routine changes all factors to "ca", causing an inconsistency
        /// with measurable impact. With this flag set, FactoredSegmenter will try to guess
        /// whether a single uppercase letter is part of an all-caps word sequence.
        /// ...This does not seem to work well, and may be removed.
        /// </summary>
        public bool UseContextDependentSingleLetterCapitalizationFactors { get; set; } = false;

        /// <summary>
        /// For sentence-level annotations, e.g. multi-lingual systems, this string
        /// declares the types of annotations. E.g. to enable sentence-level annotations
        /// for the sentence target language, e.g. "target_language=ENU", the string
        /// "target_language" is the type, and it must be declared here as a model option.
        /// </summary>      
        public string SourceSentenceAnnotationTypes { get; set; } = "";

        /// <summary>
        /// The list of source sentence annotation types.
        /// Note that this is not a property that can be specified by the user. User should instead specify SourceSentenceAnnotationTypes in above.
        /// </summary>
        [XmlIgnore]
        internal string[] SourceSentenceAnnotationTypeList => SourceSentenceAnnotationTypes != null ?
                                                              SourceSentenceAnnotationTypes.Split(new string[] { ";" }, StringSplitOptions.RemoveEmptyEntries) :
                                                              new string[0];

        // system-managed options persisted to file follow; not to be specified by user

        /// <summary>
        /// if false then skip SentencePiece. If true, then SPM model file is FS model path s/\.model$/\.fsm/
        /// </summary>
        public bool? UseSentencePiece { get; set; }
    }

    /// <summary>
    /// Class to hold all parameters for the FactoredSegmenter training tool.
    /// </summary>
    public class FactoredSegmenterModelTrainConfig : SegmenterTrainConfigBase, IFactoredSegmenterConfig
    {
        /// <summary>
        /// options persisted with the model, e.g. whether to use certain factors
        /// </summary>
        public FactoredSegmenterModelOptions ModelOptions { get; set; } = new FactoredSegmenterModelOptions(); 
        /// <summary>
        /// Number of sentences to use for determining the Marian vocab and for training
        /// the underlying SentencePiece model. Normally set to 10 million.
        /// This many sentences are sampled from the training corpus.
        /// For joint training, this is the total number of sentences across both languages.
        /// </summary>
        public override int? TrainingSentenceSize { get; set; }
        /// <summary>
        /// Only keep SentencePiece units ("pieces") with at least this many observations
        /// in the entire training set. Any unit with fewer observations will be represented
        /// as multiple shorter pieces. The rationale is that too rare observations will
        /// not get a properly trained embedding.
        /// The total Marian vocabulary consists of these pieces plus single characters.
        /// If TrainingSentenceSize is set, only a subset is processed. In this case,
        /// this count is adjusted automatically internally accordingly.
        /// </summary>
        public int MinPieceCount { get; set; } = 0;
        /// <summary>
        /// Only keep single characters with at least this many observations in the entire
        /// training data. Any character sequence that is not covered by units in the
        /// SentencePiece vocabulary will be represented as single characters.
        /// Many of these single characters are very rare, e.g. graphical characters
        /// or Cyrillic characters in a Chinese corpus, and cannot be learned properly.
        /// This parameter allows to eliminate rare characters from the vocab (they will
        /// be treated as unrepresentable, which presently means UNK).
        /// This threshold needs to be smaller than MinPieceCount to have an effect.
        /// If TrainingSentenceSize is set, only a subset is processed. In this case,
        /// this count is adjusted automatically internally accordingly.
        /// </summary>
        public int MinCharCount  { get; set; } = 0;
        /// <summary>
        /// Config for the underlying SentencePiece training (or null to indicate to not use SentencePiece).
        /// </summary>
        public SentencePieceTrainConfig SentencePieceTrainingConfig { get; set; } = new SentencePieceTrainConfig();
        // @BUGBUG: now ^^ this is created by default, so there is no way to turn it off -> @TODO: ModelOptions->UseSentencePiece = false says 'ignore this'
    }

    /// <summary>
    /// Class to hold all parameters for the FactoredSegmenter encoding tool.
    /// </summary>
    public class FactoredSegmenterEncodeConfig : SegmenterEncodeConfigBase, IFactoredSegmenterConfig
    {
        public SentencePieceEncodeConfig SentencePieceEncodeConfig { get; set; } // for the underlying SentencePiece module

        // for debugging:
        public int CheckEvery { get; set; } = 100; // decode each N-th encoded sentence and verify against source
    }

    /// <summary>
    /// Class to hold all parameters for the FactoredSegmenter decoding tool.
    /// </summary>
    public class FactoredSegmenterDecodeConfig : SegmenterDecodeConfigBase, IFactoredSegmenterConfig
    {
        SentencePieceDecodeConfig SentencePieceDecodeConfig { get; set; } // for the underlying SentencePiece module
    }
}
