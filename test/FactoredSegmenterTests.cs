namespace TextSegmentation.Segmenter.FactoredSegmenter_GitSubmodule.src.Test
{
    using Microsoft.MT.Common.Tokenization;
    using Microsoft.MT.Common.Tokenization.Segmenter;
    using Microsoft.MT.TextSegmentation.SpanFinder;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using System.Diagnostics.CodeAnalysis;
    using System.IO;
    using System.Linq;
    using System.Collections.Generic;

    /// <summary>
    /// Unit tests.
    /// A test that comprehensively tests the Encode()/Decode() output is part of ParallelSegmenter.
    /// </summary>
    [TestClass]
    [ExcludeFromCodeCoverage]
    public class FactoredSegmenterTests
    {
        // helper to generate a set of models to test
        public IEnumerable<FactoredSegmenterCoder> ModelsToTest(bool includeInlineFixes = true)
        {
            var models = new List<FactoredSegmenterCoder>();

            if (includeInlineFixes)
                models.Add(FactoredSegmenterCoder.CreateForTest(inlineFixes: true));

            models.AddRange(new[] // test multiple model sets of model options
            {
                    // uncomment this one to debug an actual SPM model
                    //FactoredSegmenterCoder.CreateForTest(@"\\mt-data-04\humanparity_tier_1\TeacherStage2Systems\enu\kor\2019_04_30_05h_47m_08s_FS_4repl\final\enu.kor.teacher.fsm", serializeIndicesAndUnrepresentables: serializeIndicesAndUnrepresentables),                    
                    FactoredSegmenterCoder.CreateForTest(),
                    FactoredSegmenterCoder.CreateForTest(sourceSentenceAnnotationTypes: new[]{ "target_language", "politeness" }),
                    FactoredSegmenterCoder.CreateForTest(singleLetterCaseFactors: true,
                                                         distinguishInitialAndInternalPieces: true,
                                                         serializeIndicesAndUnrepresentables: true,
                                                         rightWordGlue: true)
            });

            return models;

        }
        // check whether encoding is reversible, and whether number of tokens before SPM is right
        // Note that this does not test SentencePiece segmentation, and also does not check against a full ground truth result.
        [TestMethod]
        public void ReversibilityAndBasicBreakingTests()
        {
            foreach (var fsm in ModelsToTest())
            {
                // @TODO: handle weird cases [Arul]
                //  - In Hindi people often use the colon char (incorrectly) instead of the very similar looking diacritic. Ideally we should not break it from the words,
                //  - Similarly in Hebrew they often use the " double quote instead of the very similar looking letter
                Assert.IsFalse(fsm.Test("They sent a tax to Ayodhya because we had defeated them in that famous 'Ashomedha' to rend it.", annotatedSpans: new List<AnnotatedSpan> {
                        new AnnotatedSpan(12, 14, AnnotatedSpanClassType.PhraseFix, decodeAs: "First Class"), new AnnotatedSpan(27, 7, AnnotatedSpanClassType.PhraseFix, decodeAs: "Economy Class") }));  // class encoding
                Assert.IsTrue(fsm.Test("-<<<>>>{{{}}}", numSegmentsBeforeSPM: 9));  // < and { used in special tokens must be single-char tokens if in prefix position
                Assert.AreEqual(fsm.Decode(fsm.Encode("\u2581\u2581\u2581\u2581\u2581\u2581\u2581").ToString()).ToString(), "_______"); // SentencePiece space marker \u2581. Currently, this one case does NOT round-trip (but it trips up SentencePiece)
                Assert.IsFalse(fsm.Test("Tag <b>bold</b> yeah<br>! W<b>o</b>rd <br> here.", annotatedSpans: new List<AnnotatedSpan> {
                        new AnnotatedSpan( 4, 3, null, encodeAsIf: ""), new AnnotatedSpan(11, 4, null, encodeAsIf: ""), new AnnotatedSpan(20, 4, null, encodeAsIf: ""),
                        new AnnotatedSpan(27, 3, null, encodeAsIf: ""), new AnnotatedSpan(31, 4, null, encodeAsIf: ""), new AnnotatedSpan(38, 4, null, encodeAsIf: "") }));  // check directedness of space
                Assert.IsFalse(fsm.Test("Añadido un artículo acerca de la banda de Leisha <c0> .", annotatedSpans: new List<AnnotatedSpan> { new AnnotatedSpan(49, 4, null, encodeAsIf: "") }));
                // ^^ must be IsFalse since the tag is removed in the decoded string
                Assert.IsTrue(fsm.Test("(ง'̀-'́) ง", numSegmentsBeforeSPM: 9));  // really creative Emoji with Thai character followed by apostrophe followed by accent combiner
                Assert.IsTrue(fsm.Test("A photo posted by ⓐⓝⓓⓡⓔⓨ ⓟⓞⓝⓞⓜⓐⓡⓔⓥ✔️ (@a_ponomarev) on Mar 30, 2015 at 4:05am PDT", numSegmentsBeforeSPM: 41)); // letters in circles should not get capitalization factor
                Assert.IsTrue(fsm.Test("1°C! This is a test, iPods cost    $3.14, or ९३ or 二十 at 13¾°C, for camelCase, PascalCase, and NSStrings, plus a longword.", numSegmentsBeforeSPM: 70));
                Assert.IsTrue(fsm.Test("✔" + "️\ufe0f" + "写真からデータをキャプチャし、外出先のデータ解析を行います。", numSegmentsBeforeSPM: 17)); // contains VARIATION SELECTOR-16 (Emoji style)
                Assert.IsTrue(fsm.Test("I WOKE up! This is A SHAME!", numSegmentsBeforeSPM: 15));
                Assert.IsTrue(fsm.Test("I wake up at 16:45...", numSegmentsBeforeSPM: 14));
                Assert.IsTrue(fsm.Test(""));  // must handle empty strings gracefully
                Assert.IsTrue(fsm.Test("0\u0948", numSegmentsBeforeSPM: 2));  // Devanaagari diacritic applied to digit: must split off diigt
#if true        // breaking Hiragana from Kanji
                Assert.IsTrue(fsm.Test("超ハッピーな人です。", numSegmentsBeforeSPM: 6));  // should break Katakana vs. Han
                Assert.IsTrue(fsm.Test("飲む", numSegmentsBeforeSPM: 2));  // should break Hiragana vs. Han
#else           // not breaking Hiragana from Kanji; works a little worse
                Assert.IsTrue(fsm.Test("超ハッピーな人です。", numSegmentsBeforeSPM: 4));  // should break Katakana vs. Han
                Assert.IsTrue(fsm.Test("飲む", numSegmentsBeforeSPM: 1));  // should not break Hiragana vs. Han
#endif
                Assert.IsTrue(fsm.Test("アメリカ，いいえ", numSegmentsBeforeSPM: 3));  // should use continuous script
                Assert.IsTrue(fsm.Test("२०१४ से २०१९ तक", numSegmentsBeforeSPM: 13)); // should split the digits (unicode 0x0966 to 096F)
                Assert.IsTrue(fsm.Test("2014 से 2019 तक।॥", numSegmentsBeforeSPM: 14)); // Hindi Dandas are punctuation
                Assert.IsTrue(fsm.Test("Ａ１〇十"));
                Assert.IsTrue(fsm.Test("Hello World"));
                Assert.IsTrue(fsm.Test("但它只是两个不同的名字相同函数 - 正切的反函数。"));
                Assert.IsTrue(fsm.Test("文件全球博客六.二十年is a long time"));
                Assert.IsTrue(fsm.Test("Before concluding, I should like to tell Mr Savary that we are going to have a special debate tomorrow on the consequences of the storms which have struck France, Austria and Germany,"));
                Assert.IsTrue(fsm.Test("٠٫٢٥ ¾ ௰ ¹ ① ⒈ Ⅹ 六", numSegmentsBeforeSPM: 18));
            }
        }

        /// <summary>
        /// Test that properties on decoded segments are set correctly. In particular, this looks at 
        /// behavior of WordEnd and WordBegin flags at the boundary of continuous and non-continuous 
        /// substrings.
        /// </summary>
        [TestMethod]
        [ExcludeFromCodeCoverage]
        public void DecodeIntoConsecutiveSegmentsTest()
        {
            void CompareDecodedSegments(FactoredSegmenterCoder fsm, string source, IList<AnnotatedSpan> spans, IList<DecodedSegment> expected)
            {
                var encoded = fsm.Encode(source, spans?.ToList());
                var decoded = fsm.Decode(encoded.ToString(), encoded.DecoderPackage).Tokens.ToArray();

                Assert.IsTrue(expected.SequenceEqual(decoded));
            }

            var examples = new (string Source, IList<AnnotatedSpan> Spans, IList<DecodedSegment> Expected)[]
            {
                    // Tests that adjacent continuous-script and non-continuous script segments have correctly set WordToken and SpacingWord boundaries.
                    (Source: "English中文English", Spans: null, Expected: new DecodedSegment[]
                    {
                        new DecodedSegment("English", isWordTokenStart: true, isWordTokenEnd: true, sourceLinks: null, isForceDecode: false, isSpacingWordStart: true, isSpacingWordEnd: true),
                        new DecodedSegment("中文", isWordTokenStart: true, isWordTokenEnd: true, sourceLinks: null, isForceDecode: false, isSpacingWordStart: true, isSpacingWordEnd: true),
                        new DecodedSegment("English", isWordTokenStart: true, isWordTokenEnd: true, sourceLinks: null, isForceDecode: false, isSpacingWordStart: true, isSpacingWordEnd: true),
                    }),

                    // Tests WordToken and SpacingWord boundaries around tags and DecodeAs segments
                    (Source: "中文<c0>microsoft.com</c0>",
                    Spans: new AnnotatedSpan[]
                    {
                        new AnnotatedSpan(2, 4, null, encodeAsIf: ""),                                          // <c0>
                        new AnnotatedSpan(6, 13, AnnotatedSpanClassType.PhraseFix, decodeAs: "microsoft.com"),  // "microsoft.com" is passed through
                        new AnnotatedSpan(19, 5, null, encodeAsIf: "")                                          // </c0>
                    },
                    Expected: new DecodedSegment[]
                    {
                        new DecodedSegment("中文", isWordTokenStart: true, isWordTokenEnd: true, sourceLinks: null, isForceDecode: false, isSpacingWordStart: true, isSpacingWordEnd: true),
                        new DecodedSegment("microsoft.com", isWordTokenStart: true, isWordTokenEnd: true, sourceLinks: null, isForceDecode: true, isSpacingWordStart: true, isSpacingWordEnd: true),
                    }),
            };

            foreach (var fsm in ModelsToTest())
            {
                // The logic of this test does not apply to inline fixes, which
                // does not round-trip since it contains both source and target
                // in the encoded and decoded tokens.
                if (fsm.ModelForTestingOnly.ModelOptions.InlineFixes)
                    continue;
                foreach (var example in examples)
                {
                    CompareDecodedSegments(fsm, example.Source, example.Spans, example.Expected);
                }
            }
        }

        // reversibility test on a known set of "naughty Unicode strings"
        // [https://github.com/minimaxir/big-list-of-naughty-strings]
        [TestMethod]
        public void ReversibilityAndBasicBreakingTestsOnNaughtyData()
        {
            const string blnsPath = @"Segmenter\Test\blns.txt";
            foreach (var serializeIndicesAndUnrepresentables in new[] { false, true })
            {
                var fsm = FactoredSegmenterCoder.CreateForTest(serializeIndicesAndUnrepresentables: serializeIndicesAndUnrepresentables);
                foreach (var line in File.ReadLines(blnsPath)) // (no need to bother skipping comments and empty lines)
                {
                    Assert.IsTrue(fsm.Test(line));
                }
            }
        }

        // run training
        // A simple test that executes the training code path. The resulting model is not
        // compared to a reference, so this is more useful during debugging for manual
        // inspection.
        // It does, however, test encoding of unrepresentable chars.
        [TestMethod]
        public void RunTraining()
        {
            foreach (var options in from m in ModelsToTest() select m.ModelForTestingOnly.ModelOptions) // (only read out the options from test models, which we then modify further)
            {
                options.UseSentencePiece = null;
                var config = new FactoredSegmenterModelTrainConfig
                {
                    ModelOptions = options,
                    MinCharCount = 2, // "a" and "," become OOVs
                    SentencePieceTrainingConfig = null
                };
                var input = new string[]
                {
                        "This is a test text for this module.",     // "a" occurs less than MinCharCount -> unrepresentable
                        "I think it is not very complex. I think.", // "I" as an example for single-character factor
                        "This is mostly for testing that the thing actually runs, and for manual inspection of the generated vocab file."
                };
                IEnumerable<Dictionary<string, string>> sourceSentenceAnnotations = null;
                if (options.SourceSentenceAnnotationTypeList.Any())
                {
                    sourceSentenceAnnotations = new Dictionary<string, string>[] {
                            new Dictionary<string, string>{ { "target_language", "ENU" }, { "politeness", "nice"} },
                            new Dictionary<string, string>{ { "target_language", "ENU" }, { "politeness", "rude"} },
                            new Dictionary<string, string>{ { "target_language", "FRA" }, { "politeness", "nice"} }
                        };
                }
                var model = FactoredSegmenterModel.Train(config, input, sourceSentenceAnnotations, fsmModelPath: null, spmBinDir: null);
                var fsm = new FactoredSegmenterCoder(new FactoredSegmenterCoderConfig { Model = model });
                Assert.IsTrue(fsm.Test("Also A Test!"));         // note: unrepresentable 'a' (< MinCharCount) and '!' (unseen)
                Assert.IsTrue(fsm.Test("𠈓 is a surrogate...")); // example of an unrepresentable surrogate pair
                if (options.SourceSentenceAnnotationTypes != null && options.SourceSentenceAnnotationTypes.Any())
                {
                    Assert.IsTrue(fsm.Test("Hooray!", sourceSentenceAnnotations: sourceSentenceAnnotations?.First()));
                    Assert.IsTrue(fsm.Test("", sourceSentenceAnnotations: sourceSentenceAnnotations?.First())); // empty string once caused an issue
                    var knownSentenceLevelAnnotations = model.KnownSentenceLevelAnnotations;
                    Assert.AreEqual((from type in knownSentenceLevelAnnotations.Keys
                                     from values in knownSentenceLevelAnnotations[type]
                                     select values).Count(), 4);
                }
            }
        }

        /// <summary>
        /// Test insertion of missing phrasefixes in the target.
        /// </summary>
        [TestMethod]
        public void InsertMissingPhrasefixTest()
        {
            var emptyAlignment = new Common.MT.Segments.Alignment();

            // if there are phrasefix tokens in the source but not the target, they should be added back in heuristically.
            foreach (var fsm in ModelsToTest(includeInlineFixes: false))
            {
                var encodedWithPF = fsm.Encode("A test.", new List<AnnotatedSpan> { new AnnotatedSpan(2, 4, AnnotatedSpanClassType.PhraseFix, AnnotatedSpanInstructions.ForceDecodeAs, "fix") });
                var encodedNoPF = fsm.Encode("A test.");

                // if there are no phrasefixes in the source, none will need to be added to the target.
                var decodedNoSourcePF = fsm.Decode(encodedNoPF.TokenStrings, emptyAlignment, encodedNoPF.DecoderPackage);
                Assert.AreEqual(0, decodedNoSourcePF.Tokens.Count(tok => tok.IsForceDecode));

                // this simulates a situation where there is a phrasefix token in the source, but none appeared in the target
                var decodedWithSourcePF = fsm.Decode(encodedNoPF.TokenStrings, emptyAlignment, encodedWithPF.DecoderPackage);
                Assert.AreEqual(1, decodedWithSourcePF.Tokens.Count(tok => tok.IsForceDecode));
            }
        }

        /// <summary>
        /// Test that we can handle the case where there's a null index factor on a phrasefix token in the output.
        /// </summary>
        [TestMethod]
        public void InsertMissingPhrasefix_MangledIndexTokensTest()
        {
            var emptyAlignment = new Common.MT.Segments.Alignment();
            var serializeIndicesFsm = FactoredSegmenterCoder.CreateForTest(singleLetterCaseFactors: true,
                                                         distinguishInitialAndInternalPieces: true,
                                                         serializeIndicesAndUnrepresentables: true,
                                                         rightWordGlue: true);

            // {▁A|scu|wb|we ▁{word}|cn|wb|we|classphrasefix <9> <#> .|gl+|gr-}
            var encodedWithPFSerialized = serializeIndicesFsm.Encode("A test.", new List<AnnotatedSpan> { new AnnotatedSpan(2, 4, AnnotatedSpanClassType.PhraseFix, AnnotatedSpanInstructions.ForceDecodeAs, "fix") });

            // Reusing the encoded string as decoded, but stripping the digit tokens will produce a phrasefix token with
            // null index factor. 
            // {▁A|scu|wb|we ▁{word}|cn|wb|we|classphrasefix .|gl+|gr-}
            var mangledDecodedTokenStrings = serializeIndicesFsm.StripDigitTokensForTest(encodedWithPFSerialized.TokenStrings);

            // The output contains a malformed phrasefix. This should be deleted, but a correct one (matching the input)
            // should be inserted. It's difficult to verify directly that this is happening without breaking encapsulation,
            // but this will exercise the relevant code paths.
            var mangledDecoded = serializeIndicesFsm.Decode(mangledDecodedTokenStrings, emptyAlignment, encodedWithPFSerialized.DecoderPackage);

            Assert.AreEqual(1, mangledDecoded.Tokens.Count(tok => tok.IsForceDecode));
        }
    }
}
