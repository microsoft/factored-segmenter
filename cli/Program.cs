using Common.Collections.Extensions;
using Common.Utils;
using Microsoft.MT.Common.Tokenization;
using Microsoft.MT.Segmentation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace factored_segmenter
{
    class Program
    {
        /// <summary>
        /// Command-line format:
        ///   factored-segmenter train|encode|decode [--option]* [input file|-]
        /// </summary>
        static void Main(string[] args)
        {
            var (GetAndConsumeArg, GetArg) = IterateArgs(args);
            var action = GetAndConsumeArg();
            if (action != "train" && action != "encode" && action != "decode")
                BadArgument("The first argument must be 'train', 'encode', or 'decode'");

            // parse options
            string dataOutPath = "-";
            string modelPath = null;
            string vocabOutputPath = null;
            string fieldSeparator = null;
            bool quiet = false;
            FactoredSegmenterModelTrainConfig newModelConfig = new FactoredSegmenterModelTrainConfig();
            while (GetArg() != null && ((GetArg().StartsWith("-") && GetArg().Length > 1) || GetArg().StartsWith("--")))  // --option, -o, and --
            {
                bool GetBoolArg() // helper to parse bool options have an optional "true" or "false" follow them
                    => GetArg() == null || (GetArg() != "true" && GetArg() != "false") || GetAndConsumeArg() == "true";
                var option = GetAndConsumeArg();
                // common args
                if ((option == "-o" || option == "--output") && action != "train") // output stream for encode and decode
                    dataOutPath = GetAndConsumeArg();
                else if (option == "-m" || option == "--model") // model path: output for train, input for encode/decode
                    modelPath = GetAndConsumeArg();
                else if ((option == "-v" || option == "--marian-vocab") && action == "train")
                    vocabOutputPath = GetAndConsumeArg();
                else if (option == "--quiet") // avoid unnecessary logging
                    quiet = GetBoolArg();
                else if (option == "-F") // field separator, e.g. set to "\t" to process TSV format
                    fieldSeparator = Regex.Unescape(GetAndConsumeArg()); // unescape so that we can pass \t
                // new-model args
                else if (option == "--right-word-glue")
                    newModelConfig.ModelOptions.RightWordGlue = GetBoolArg();
                else if (option == "--distinguish-initial-and-internal-pieces")
                    newModelConfig.ModelOptions.DistinguishInitialAndInternalPieces = GetBoolArg();
                else if (option == "--split-han")
                    newModelConfig.ModelOptions.SplitHan = GetBoolArg();
                else if (option == "--single-letter-case-factors")
                    newModelConfig.ModelOptions.SingleLetterCaseFactors = GetBoolArg();
                else if (option == "--serialize-indices-and-unrepresentables")
                    newModelConfig.ModelOptions.SerializeIndicesAndUnrepresentables = GetBoolArg();
                else if (option == "--inline-fixes")
                    newModelConfig.ModelOptions.InlineFixes = GetBoolArg();
                else if (option == "--inline-fix-use-tags")
                    newModelConfig.ModelOptions.InlineFixUseTags = GetBoolArg();
                else if (option == "--no-sentence-piece")
                    newModelConfig.SentencePieceTrainingConfig = null;
                // training args
                else if (option == "--vocab-size" && action == "train")
                    newModelConfig.SentencePieceTrainingConfig.VocabSize = int.Parse(GetAndConsumeArg());
                else if (option == "--character_coverage" && action == "train")
                    newModelConfig.SentencePieceTrainingConfig.CharacterCoverage = double.Parse(GetAndConsumeArg());
                else if (option == "--training-sentence-size" && action == "train")
                    newModelConfig.TrainingSentenceSize = int.Parse(GetAndConsumeArg());
                else if (option == "--min-piece-count" && action == "train")
                    newModelConfig.MinPieceCount = int.Parse(GetAndConsumeArg());
                else if (option == "--min-char-count" && action == "train")
                    newModelConfig.MinCharCount = int.Parse(GetAndConsumeArg());
                // other
                else if (option == "--") // -- ends option processing
                    break;
                else
                    BadArgument($"Unknown option {option}");
            }

            // parse remaining arguments (one or more input files)
            var inputPaths = new List<string>();
            while (GetArg() != null)
                inputPaths.Add(GetAndConsumeArg());
            if (!inputPaths.Any()) // none given: read from stdin
                inputPaths.Add("-");

            // open all input files
            var streams = from inputPath in inputPaths
                          select inputPath != "-" ?
                              new StreamReader(inputPath, encoding: Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1000000) :
                              Console.In;

            if (action == "train")
            {
                if (!quiet)
                    Log($"Creating model {modelPath} from input file(s) {" ".JoinItems(inputPaths)} ...");
                if (!modelPath.EndsWith(".fsm")) // @TODO: do this inside Train() where we create the temp pathnames
                    BadArgument($"Extension .fsm is required for model path {modelPath}");
                var lines = from stream in streams
                            from line in stream.ReadLines()
                            select line;
                CreateDirectoryFor(modelPath); // @TODO: do this inside Train()
                var model = FactoredSegmenterModel.Train(newModelConfig, lines, sourceSentenceAnnotations: null, fsmModelPath: modelPath, spmBinDir: SentencePieceManaged.SpmBinaryDirPath);

                // save the model
                // The SentencePiece model is embedded in 'model'; it is not a separate file.
                model.Save(modelPath);
                if (!quiet)
                    Log($"Model file written to {modelPath}");

                // save the vocab for Marian consumption
                if (model.FactorSpec != null && vocabOutputPath != null)
                {
                    File.WriteAllLines(vocabOutputPath, model.FactorSpec, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    if (!quiet)
                        Log($"Marian vocabulary file written to {vocabOutputPath}");
                }
            }
            else // action == "encode" or "decode"
            {
                if (!quiet)
                    Log($"Processing input file(s) {" ".JoinItems(inputPaths)} with model {modelPath} ...");
                var lines = from stream in streams.ToList()  // ToList() eagerly opens all streams, to test upfront if all files are found
                            from line in stream.ReadLines()
                            select line;
                newModelConfig.ModelOptions.UseSentencePiece = false;
                var coderConfig = modelPath != null ?
                    new FactoredSegmenterCoderConfig
                    {
                        ModelPath = modelPath
                    } :
                    new FactoredSegmenterCoderConfig  // no model specified: use untrained virgin model (without SentencePiece)
                    {
                        Model = new FactoredSegmenterModel(newModelConfig.ModelOptions)
                    };
                var coder = new FactoredSegmenterCoder(coderConfig);

                // write loop
                if (!quiet)
                    Log($"Writing processed lines to {dataOutPath} ...");
                CreateDirectoryFor(dataOutPath);
                var outStream = dataOutPath != "-" ?  // open output stream (UTF-8 without BOM)
                    new StreamWriter(dataOutPath, append: false, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1000000) :
                    Console.Out;
                var linesProcessed = 0;
                string ProcessLine(string line)
                {
                    try
                    {
                        return action == "encode" ?
                               " ".JoinItems(coder.Encode(line).TokenStrings) :  // encode
                               coder.Decode(line).ToString();                    // decode
                    }
                    catch (Exception e)
                    {
                        Log($"Failed to {action} input: {line}");
                        Log($"Exception: {e.ToString()}");
                        return "";  // back off to empty string, so that we can continue
                    }
                }
                foreach (var line in lines)
                {
                    string processedLine = fieldSeparator == null ?
                        processedLine = ProcessLine(line) :
                        processedLine = fieldSeparator.JoinItems(from field in line.Split(fieldSeparator) select ProcessLine(field));
                    //Log($"{command} IN: {line} --> OUT: {processedLine}");
                    outStream.WriteLine(processedLine);
                    // @BUGBUG: Write errors are not caught, at least when writing to a pipe via stdout.
                    linesProcessed++;
                    if (!quiet && linesProcessed % 1000000 == 0)
                        Log($"Completed processing of {linesProcessed:#,##0} lines so far.");
                }
                if (!quiet)
                    Log($"Completed processing of {linesProcessed:#,##0} lines.");

                outStream.Flush(); // hoping to elicit an exception in case flushing fails
                outStream.Close();
            }
        }

        static void Log(string what) => Logger.WriteLine(string.Format("{0:yyyy/MM/dd HH:mm:ss.fff} factored-segmenter: ", DateTime.Now) + what);

        static void BadArgument(string what)
        {
            Log(what);
            Environment.Exit(1);
        }

        static (Func<string> GetAndConsumeArg, Func<string> GetArg) IterateArgs(string[] args)
        {
            var e = args.GetEnumerator();
            var b = e.MoveNext();
            return (GetAndConsumeArg: () =>
                    {
                        if (!b)
                            BadArgument("At least one more argument was expected.");
                        var res = e.Current as string;
                        b = e.MoveNext(); // b is boxed, so this persists across calls
                        return res;
                    },
                    GetArg: () => b ? e.Current as string : null);
        }

        static DirectoryInfo CreateDirectoryFor(string filePath)
            => filePath != "-" ? Directory.CreateDirectory(Path.GetDirectoryName(filePath)) : null;
    }
}
