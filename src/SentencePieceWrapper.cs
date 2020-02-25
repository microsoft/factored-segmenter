using Common.Collections;
using Common.Collections.Extensions;
using Common.Contracts;
using Common.IO;
using Common.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Microsoft.MT.Common.Tokenization
{
    /// <summary>
    /// Wrapper for SentencePiece that supports
    ///  - training an SPM model via invoking the spm_train executable
    ///  - encoding of words as pieces via an in-memory object/lambda
    /// </summary>
    public class SentencePieceModel
    {
        const string spmModelExt = ".model"; // these are required/hard-coded by the spm_train tool
        const string spmVocabExt = ".vocab";

        // model data
        public byte[] Bytes { get; }

        /// <summary>
        /// Construct an SPM model from file.
        /// </summary>
        public static SentencePieceModel Load(string path)
        {
            return new SentencePieceModel(File.ReadAllBytes(path));
        }

        /// <summary>
        /// Construct an SPM model from a byte array.
        /// </summary>
        public SentencePieceModel(byte[] modelBlob)
        {
            Bytes = modelBlob;
        }

        /// <summary>
        /// Construct an SPM model from data; that is, train one.
        /// The input is passed as an IEnumerable or a ParallelQuery of lines of raw plain-text.
        /// The model is returned as a binary blob (for later use in encoding/decoding).
        /// Underneath, this uses the spm_train executable, which needs to store the model as a file. That location is
        /// passed in as 'tempSPMModelPath'. These output files are temporary and local to this function, but
        /// it is useful to keep them around for diagnostics and debugging; they are not (meant to be) used after this.
        /// 'minPieceCount' allows to set a minimum observation count for word pieces. spm_train does not support this,
        /// so we emulate/approximate it by running spm_train twice.
        /// </summary>
        public static SentencePieceModel Train<Enumerable>(Enumerable tokenStrings, string tempSPMModelPath,
            SentencePieceTrainConfig spmParams, int minPieceCount, string spmBinDir)
            where Enumerable : IEnumerable<string> // using template so we won't loose parallelism (is this needed?)
        {
            Sanity.Requires(tempSPMModelPath.EndsWith(spmModelExt), $"FactoredSegmenter SentencePiece model path must end in {spmModelExt}");
            var modelPrefix = tempSPMModelPath.Substring(0, tempSPMModelPath.Length - spmModelExt.Length);

#if false   // helper during debugging of final Training stage when models already exist
            LoadSPMModelFiles(modelPrefix, out var spmModelBlob, out var spmVocab);
#else

            // write the tokens to a temp file
            var tempInputDataPath = modelPrefix + ".data";
            Logger.WriteLine($"FactoredSegmenter: Writing to temp file {tempInputDataPath} for SPM training...");
            AtomicFileWriter.Save(tempInputDataPath, tmpPath => File.WriteAllLines(tmpPath, tokenStrings, new UTF8Encoding()));
            // atomic writing allows the impatient user to know when the writing has completed and spm_train has taken over

            // invoke spm_train
            SPMTrain(tempInputDataPath, modelPrefix, spmParams, spmBinDir, null);

            // fetch the content of the generated .model and .vocab file into in-memory data structures
            // After this, the spm_train-generated files are no longer used; and only kept for debugging purposes.
            LoadSPMModelFiles(modelPrefix, out var spmModelBlob, out var spmVocab);

            // enforce minimum piece-count constraint
            if (minPieceCount > 1)
            {
                // encode the SPM training data and count each token's occurence
                Logger.WriteLine($"FactoredSegmenter: Minimum-count constraint ({minPieceCount}), counting SPM tokens...");
                var coder = new SentencePieceCoder(new SentencePieceCoderConfig { SentencePieceModel = new SentencePieceModel(spmModelBlob) });
                var counts = CountEncodedTokens(tempInputDataPath, coder);
                File.WriteAllLines(tempSPMModelPath + $".{spmVocab.Length}.counts", // save it for diagnostics only
                    from kvp in counts orderby -kvp.Value, kvp.Key select $"{kvp.Key}\t{kvp.Value}");
                // count number of SPM vocab items that should be kept (above the threshold or single character which we always keep)
                var spmVocabSet = new HashSet<string>(spmVocab);
                int adjustedVocabSize = counts.Count(kvp => spmVocabSet.Contains(kvp.Key) && (kvp.Key.Length == 1 || kvp.Value >= minPieceCount));
                // if there are units below the threshold, reduce the SPM vocab size and retrain
                if (adjustedVocabSize < spmVocab.Length)
                {
                    Logger.WriteLine($"FactoredSegmenter: Only {adjustedVocabSize} out of {spmVocab.Length} sentence pieces have {minPieceCount} or more observations." +
                                     $" Retraining SPM model with reduced vocabSize {adjustedVocabSize}");
                    // invoke spm_train a second time
                    SPMTrain(tempInputDataPath, modelPrefix, spmParams, spmBinDir, adjustedVocabSize);
                    LoadSPMModelFiles(modelPrefix, out spmModelBlob, out spmVocab); // reload the new model
                }
                // count once again for diagnostics only
                Logger.WriteLine($"FactoredSegmenter: Re-counting SPM tokens after reduction to {adjustedVocabSize}...");
                coder = new SentencePieceCoder(new SentencePieceCoderConfig { SentencePieceModel = new SentencePieceModel(spmModelBlob) });
                counts = counts = CountEncodedTokens(tempInputDataPath, coder);
                File.WriteAllLines(tempSPMModelPath + $".{adjustedVocabSize}.counts", // save for diagnostics only
                    from kvp in counts orderby -kvp.Value, kvp.Key select $"{kvp.Key}\t{kvp.Value}");
            }

            // delete the temp file   --except if it failed, so user can double-check what's going on
            // commented out temporarily to aid debugging
            //File.Delete(tempPath);
#endif

            return new SentencePieceModel(spmModelBlob);
        }

        // helper to count encoded tokens
        private static Dictionary<string, int> CountEncodedTokens(string tempInputDataPath, SentencePieceCoder coder)
        {
            var counts = new Dictionary<string, int>();
            var pieces = from s in File.ReadLines(tempInputDataPath).AsParallel() // note: AsParallel() makes things out of order
                         let cutList = coder.Split(s)
                         from range in (cutList == null) ? new[] { (0, s.Length) } : cutList.Bigrams()
                         select s.Substring(range.Item1, range.Item2 - range.Item1);
            foreach (var piece in pieces)
            {
                counts.TryGetValue(piece, out var count);
                counts[piece] = count + 1;
            }
            return counts;
        }

        // invoke spm_train tool
        // Reads input data from file, and creates model and vocab to modelPrefix.model and .vocab, respectively.
        private static void SPMTrain(string inputPath, string modelPrefix, SentencePieceTrainConfig spmParams, string spmBinDir, int? vocabSize)
        {
            // e.g.
            // spm_train \
            //    --input=/philly/wu3/msrmt/fseide/WMT.paracrawl/data/all.paracrawl.8M.norm.$units.ende.sub \
            //    --model_prefix=/philly/wu3/msrmt/fseide/WMT.paracrawl/model/all.paracrawl.8M.norm.$units.ende \
            //    --vocab_size=32000  --character_coverage=1.0  --model_type=unigram  --shuffle_input_sentence=false
            var exe = Path.Combine(spmBinDir, "spm_train"); // (note: no .exe so that this can run on both Windows and Linux)
            var args = new List<string> { "--input", inputPath, "--model_prefix", modelPrefix };
            var extraArgs = from extraParam in new Dictionary<string, object>
                            { // @TODO: use generic Flo method that parses the struct type directly
                                ["vocab_size"] = vocabSize ?? spmParams.VocabSize,
                                ["character_coverage"] = spmParams.CharacterCoverage,
                                ["model_type"] = spmParams.ModelType.ToString().ToLower(),
                                //["shuffle_input_sentence"]   = spmParams.ShuffleInputSentence.ToString().ToLower(), // not supported in the SPM package version used in Flo
                                ["add_dummy_prefix"] = spmParams.AddDummyPrefix.ToString().ToLower(),
                                ["normalization_rule_name"] = spmParams.NormalizationRuleName.ToString().ToLower(),
                                ["split_by_whitespace"] = spmParams.SplitByWhitespace.ToString().ToLower(),
                                ["remove_extra_whitespaces"] = spmParams.RemoveExtraWhitespaces.ToString().ToLower(),
                                ["input_sentence_size"] = spmParams.InputSentenceSize,
                                ["mining_sentence_size"] = spmParams.MiningSentenceSize,
                                ["training_sentence_size"] = spmParams.TrainingSentenceSize,
                                ["seed_sentencepiece_size"] = spmParams.SeedSentencepieceSize,
                                ["max_sentence_length"] = spmParams.MaxSentenceLength
                            }
                            where extraParam.Value != null
                            let val = extraParam.Value.ToString()
                            where val != ""
                            from arg in new string[] { "--" + extraParam.Key, val }
                            select arg; // unroll into form --arg1 argval1 --arg2 argval2 ...
            args.AddRange(extraArgs);
            var envirVariables = new Dictionary<string, string> { { "LC_ALL", "C" } }; // (not sure if this matters; better safe than sorry)
            ProcessTools.RunCommand(exe, ProcessTools.ArgsToCommandLine(args), null, modelPrefix + ".log", throwOnFailure: true, envirVariables: envirVariables);
        }

        // helper to fetch .model and .vocab file written out by SPMTrain above into in-memory variables
        private static void LoadSPMModelFiles(string modelPrefix, out byte[] spmModel, out string[] spmVocab)
        {
            spmModel = File.ReadAllBytes(modelPrefix + spmModelExt);
            spmVocab = (from line in File.ReadAllLines(modelPrefix + spmVocabExt)
                        select line.Split('\t').First())                   // .vocab file has the form "TOKEN\tLOGPROB"; we only want TOKEN
                       .OrderBy(t => t.ToString(), StringComparer.Ordinal) // sort it for neatness
                       .ToArray();
        }
    }


    public class SentencePieceCoderConfig
    {
        /// <summary>
        /// The underlying native SentencePiece model
        /// </summary>
        public SentencePieceModel SentencePieceModel { get; set; }
        /// <summary>
        /// If set, then SPM will be restricted to pieces in this set.
        /// We have seen examples where the internal SPM vocab contains a few units
        /// that are not observed when encoding the SPM training set. I suspect that
        /// is because training uses a soft forward-backward method, while the
        /// re-encoding of the SPM training set uses a best path. To circumvent that
        /// situation, we pass the set of pieces determined by re-encoding the training set.
        /// </summary>
        public HashSet<string> VocabSubset { get; set; } = null;
        /// <summary>
        /// If defined, size of cache to use for split(). Currently only used for caching splits at word level when called from factored segmenter.
        /// </summary>
        public int SplitCacheSize { get; set; } = 0;
    }


    /// <summary>
    /// Shallow wrapper over the SentencePieceManaged lib for encoding and decoding,
    /// which follows our design of accepting a corresponding model as an input, and providing
    /// encode and decode functions.
    /// </summary>
    public class SentencePieceCoder
    {
        readonly Segmentation.SentencePieceManaged spm;
        private BoundedSizedLockingCache<string, int[]> m_splitCache;

        /// <summary>
        /// Construct a coder from a SentencePieceModel.
        /// @TODO: Should this also have a config object?
        /// </summary>
        /// <param name="model">Sentencepiece model to delegate calls to</param>
        /// <param name="cacheSize">Size of cache for calls to Split() (segmentation is very resource intensive)</param>
        public SentencePieceCoder(SentencePieceCoderConfig config)
        {
            spm = new Segmentation.SentencePieceManaged();
            // Save BLOB to file, since the current SentencePieceManaged wrapper can only load the model from a file,
            // @TODO: The SentencePiece native API also supports reading from a std::istream,
            //        so we should pass the blob via a a simple istream class that reads from memory, cf.
            //        https://stackoverflow.com/questions/2079912/simpler-way-to-create-a-c-memorystream-from-char-size-t-without-copying-t
            var spmTempModelPath = Path.GetTempFileName();
            File.WriteAllBytes(spmTempModelPath, config.SentencePieceModel.Bytes);
            spm.LoadModel(spmTempModelPath, config.VocabSubset?.ToArray());
            File.Delete(spmTempModelPath);
            m_splitCache = new BoundedSizedLockingCache<string, int[]>(config.SplitCacheSize);
        }

        /// <summary>
        /// Invoke SPM encode on a text line
        /// </summary>
        public string[] Encode(string line) => spm.Segment(line);

        /// <summary>
        /// Encode a word (or continuous-script segment) and return the result a list of split points.
        /// E.g. if SPM splits an input word "hello" into "hel" and "lo",
        /// this function returns (0, 3, 5). The result includes start (0) and length.
        /// If the word was not split, it returns null, to save some memory allocation overhead.
        /// Characters that cannot be represented by the sentence-piece inventory are
        /// returned as individual characters.
        /// This function is not meant to be used with unsegmented input. Its behavior for inputs
        /// that include spaces is not tested or known.
        /// </summary>
        /// <param name="s">Character sequence to split.</param>
        /// <param name="adjustForWordBegPrefix">If true, s has a leading _. Subtract 1 from every offset.</param>
        /// <returns>List of split offsets (including 0 and the string length) or null if not split.</returns>
        public int[] Split(string s, bool adjustForWordBegPrefix = false) => CachedFunction.Memoize<int[], string>(m_splitCache, s, x =>
        {
            var cutList = spm.GetSplitPoints(x);
            if (adjustForWordBegPrefix && cutList != null) // source string had leading boundary prefix--account for it
                for (int i = 1; i < cutList.Length; i++)
                    cutList[i]--;
            return cutList;
        });

        /// <summary>
        /// Invoke SPM decode on an array of pieces
        /// </summary>
        public string Decode(string[] pieces) => spm.Unsegment(pieces);
    }
}
