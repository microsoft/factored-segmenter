// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace Microsoft.MT.Common.Tokenization
{
    // types for SentencePiece
    public enum SentencePieceModelType
    {
        [XmlEnum(Name = "unigram")]
        Unigram = 0,
        [XmlEnum(Name = "bpe")]
        Bpe,
        [XmlEnum(Name = "word")]
        Word,
        [XmlEnum(Name = "char")]
        Char
    }

    public enum SentencePieceNormalizationRuleName
    {
        [XmlEnum(Name = "nmt_nfkc")]
        Nfkc = 0,
        [XmlEnum(Name = "identity")]
        Identity
    }

    // Note: The following cannot be specified by Flo users, as these are under Flo's control.
    public enum SentencePieceInputFormat
    {
        [XmlEnum(Name = "text")]
        Text = 0,
        [XmlEnum(Name = "tsv")]
        Tsv
    }

    public enum SentencePieceEncodeFormat
    {
        [XmlEnum(Name = "piece")]
        Piece = 0,
        [XmlEnum(Name = "id")]
        Id,
        [XmlEnum(Name = "proto")]
        Proto,
        [XmlEnum(Name = "nbest_piece")]
        NBest_Piece,
        [XmlEnum(Name = "nbest_id")]
        NBest_Id,
        [XmlEnum(Name = "nbest_proto")]
        NBest_Proto
    }

    public enum SentencePieceDecodeInputFormat
    {
        [XmlEnum(Name = "piece")]
        Piece = 0,
        [XmlEnum(Name = "id")]
        Id
    }

    public enum SentencePieceDecodeOutputFormat
    {
        [XmlEnum(Name = "string")]
        String = 0,
        [XmlEnum(Name = "proto")]
        Proto
    }

    /// <summary>
    /// Class to hold all parameters for the SentencePiece training tool.
    /// </summary>
    public class SentencePieceTrainConfig : SegmenterTrainConfigBase, ISentencePieceConfig
    {
        /// <summary>
        /// comma-separated list of languages this model can accept
        /// </summary>
        public string AcceptLanguage { get; set; }
        /// <summary>
        /// Add dummy whitespace at the beginning of text ( default: true )
        /// </summary>
        public bool? AddDummyPrefix { get; set; }
        /// <summary>
        /// Override BOS (&lt;s&gt;) id. Set -1 to disable BOS ( default: -1 )
        /// @BUGBUG: BosId, eosId and UnkId should not be user-specifyable, as they are controlled by Flo
        /// </summary>
        public Int32 BosId { get; set; } = -1;
        /// <summary>
        /// Character coverage to determine the minimum symbols ( default: 0.9995 )
        /// </summary>
        public double? CharacterCoverage { get; set; }
        /// <summary>
        /// Comma separated list of control symbols
        /// </summary>
        public string ControlSymbols { get; set; }
        /// <summary>
        /// Override EOS ((&lt;/s&gt;)) id. Set -1 to disable EOS. ( default: 0 )
        /// @BUGBUG: BosId, eosId and UnkId should not be user-specifyable, as they are controlled by Flo
        /// </summary>
        public Int32 EosId { get; set; } = 0;
        /// <summary>
        /// If set to false, --vocab_size is considered as a soft limit. ( default: true )
        /// </summary>
        public bool? HardVocabLimit { get; set; }
        /// <summary>
        /// Comma separated list of input sentences )  type: string 
        /// </summary>
        public string input { get; set; }
        /// <summary>
        /// Input format. Supported format is 'text' or 'tsv'. ( default: 'text' )
        /// </summary>
        public SentencePieceInputFormat? InputFormat { get; set; }
        /// <summary>
        /// Maximum size of sentences the trainer loads ( default: 10000000 )
        /// </summary>
        public Int32? InputSentenceSize { get; set; }
        /// <summary>
        /// Maximum length of sentence in bytes ( default: 2048)
        /// </summary>
        public Int32? MaxSentenceLength { get; set; }
        /// <summary>
        /// Maximum length of sentence piece ( default: 16 )
        /// </summary>
        public Int32? MaxSentencepieceLength { get; set; }
        /// <summary>
        /// Maximum size of sentences to make seed sentence piece ( default: 2000000 )
        /// </summary>
        public Int32? MiningSentenceSize { get; set; }
        /// <summary>
        /// Output model prefix
        /// </summary>
        public string ModelPrefix { get; set; }
        /// <summary>
        /// Model algorithm: unigram, bpe, word or char ( default: unigram )
        /// </summary>
        public SentencePieceModelType? ModelType { get; set; }
        /// <summary>
        /// Normalization rule name. Choose from nfkc or identity ( default: nmt_nfkc )
        /// </summary>
        public SentencePieceNormalizationRuleName? NormalizationRuleName { get; set; }
        /// <summary>
        /// Normalization rule TSV file. 
        /// </summary>
        public string NormalizationRuleTsv { get; set; }
        /// <summary>
        /// Number of EM sub-iterations ( default: 2 )
        /// </summary>
        public Int32? NumSubIterations { get; set; }
        /// <summary>
        /// Number of threads for training ( default: 16 )
        /// </summary>
        public Int32? NumThreads { get; set; }
        /// <summary>
        /// Override PAD (&lt;pad&gt;) id. Set -1 to disable PAD. ( default: -1 )
        /// </summary>
        public Int32? PadId { get; set; }
        /// <summary>
        /// Removes leading, trailing, and duplicate internal whitespace ( default: true )
        /// </summary>
        public bool? RemoveExtraWhitespaces { get; set; }
        /// <summary>
        /// The size of seed sentencepieces ( default: 1000000 )
        /// </summary>
        public Int32? SeedSentencepieceSize { get; set; }
        /// <summary>
        /// The size of self test samples ( default: 0 )
        /// </summary>
        public Int32? SelfTestSampleSize { get; set; }
        /// <summary>
        /// Keeps top shrinking_factor pieces with respect to the loss ( default: 0.75 )
        /// </summary>
        public double? ShrinkingFactor { get; set; }
        /// <summary>
        /// Use Unicode script to split sentence pieces ( default: true )
        /// </summary>
        public bool? SplitByUnicodeScript { get; set; }
        /// <summary>
        /// Use a white space to split sentence pieces ( default: true )
        /// </summary>
        public bool? SplitByWhitespace { get; set; }
        /// <summary>
        /// Maximum size of sentences to train sentence pieces ( default: 10000000 )
        /// </summary>
        public override Int32? TrainingSentenceSize { get; set; }
        /// <summary>
        /// Override UNK (&lt;unk&gt;) id. ( default: 1 )
        /// </summary>
        public Int32 UnkId { get; set; } = 1;
        /// <summary>
        /// Dummy surface string for &lt;unk&gt;. In decoding &lt;unk&gt; is decoded to `unk_surface`.
        /// @BUGBUG: BosId, eosId and UnkId should not be user-specifyable, as they are controlled by Flo
        /// </summary>
        public string UnkSurface { get; set; }
        /// <summary>
        /// If set to true, use all tokens as vocab.Valid for word/char models. ( default: false )
        /// </summary>
        public bool? UseAllVocab { get; set; }
        /// <summary>
        /// Comma separated list of user defined symbols
        /// </summary>
        public string UserDefinedSymbols { get; set; }
        /// <summary>
        /// Vocabulary size ( default: 32000 )
        /// </summary>
        public int? VocabSize { get; set; } = 32000;
    }

    /// <summary>
    /// Class to hold all parameters for the SentencePiece encoding tool.
    /// </summary>
    public class SentencePieceEncodeConfig : SegmenterEncodeConfigBase, ISentencePieceConfig
    {
        /// <summary>
        /// Smoothing parameter for sampling mode ( default: 0.5 )
        /// </summary>
        public double? Alpha { get; set; }
        /// <summary>
        /// ':' separated encoder extra options, e.g., "reverse:bos:eos"
        /// </summary>
        public string ExtraOptions { get; set; }
        /// <summary>
        /// Generates vocabulary file instead of segmentation ( default: false )
        /// Internal use only; cannot be specified by Flo user.
        /// </summary>
        public bool? GenerateVocabulary { get; set; }
        /// <summary>
        /// NBest size ( default: 10 ). Only used if OutputFormat is nbest_XXX.
        /// </summary>
        public Int32? NBest_Size { get; set; }
        /// <summary>
        /// choose from piece, id, proto, nbest_piece, nbest_id, or nbest_proto ( default: piece) 
        /// Internal use only; cannot be specified by Flo user.
        /// </summary>
        public SentencePieceEncodeFormat? OutputFormat { get; set; }
        /// <summary>
        /// Restrict the vocabulary. The encoder only emits the tokens in "vocabulary" file
        /// </summary>
        public string Vocabulary { get; set; }
        /// <summary>
        /// Words with frequency below threshold will be treated as OOV ( default: 0 )
        /// </summary>
        public Int32? VocabularyThreshold { get; set; }
    }

    /// <summary>
    /// Class to hold all parameters for the SentencePiece decoding tool.
    /// </summary>
    public class SentencePieceDecodeConfig : SegmenterDecodeConfigBase, ISentencePieceConfig
    {
        /// <summary>
        /// ':' separated encoder extra options, e.g., "reverse:bos:eos"
        /// </summary>
        public string ExtraOptions { get; set; }
        /// <summary>
        /// choose from piece, id. Default: piece
        /// Internal use only; cannot be specified by Flo user.
        /// </summary>
        public SentencePieceDecodeInputFormat? InputFormat { get; set; }
        /// <summary>
        /// choose from string or proto. Default: string
        /// Internal use only; cannot be specified by Flo user.
        /// </summary>
        public SentencePieceDecodeOutputFormat? OutputFormat { get; set; }
    }
}
