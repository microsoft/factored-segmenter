# FactoredSegmenter

FactoredSegmenter is the unsupervised text tokenizer for machine translation that underlies Microsoft Translator.
The term "FactoredSegmenter" refers to both a segmentation library and an encoding of text that aims at _factoring shared properties of words_, such as casing or spacing.

FactoredSegmenter segments words into subwords using the popular [SentencePiece](https://github.com/google/sentencepiece) library under the hood.
However, unlike SentencePiece in its common usage, spaces and capitalization are not encoded in the sub-word tokens themselves.
Instead, they are encoded in _factors_ that are attached to each token.
This allows to share model parameters across occurences of the same word in the middle of a sentence, capitalized at the start of a sentence, at the start of a sentence enclosed in parentheses or quotation marks, or in all-caps in a social-media rant.
In SentencePiece, these are all distinct tokens, which is less robust. For example, this distinction leads to poor translation accuracy for all-caps sentences.

FactoredSegmenter complements the [Marian Neural Machine Translation Toolkit](https://github.com/marian-nmt/marian), which has native support for factored modeling, and directly understands the FactoredSegmenter text encoding.

FactoredSegmenter supports so-called "phrase fixing," where specific phrases are required to be translated in a very specific way. This is supported by either allowing to replace such phrases by a fixed token (where a factor is used to distinguish multiple such phrase fixes in a single sentence), or by allowing to insert the desired target translation already in the source, where factors are used to distinguish the source from the target translation.

In addition to factors, FactoredSegmenter treats numerals specially: In FactoredSegmenter, each digit is always its own token, in every writing system. We have observed that this reliably fixes a large class of translation errors for numerals, especially when translating between different numeric systems (such as Arabic numbers to Chinese).

Lastly, FactoredSegmenter provides a mechanism to handle unknown characters, such as rare graphical characters, by encoding them by their Unicode character code in a form that a translation system can learn to copy through.

## Why Factors?

Consider the word "hydroxychloroquine." Whether it occurs at the beginning of the word (where it would normally be capitalized) or within the sentence, or whether it appears after a quotation mark (where it is lower-case but there is no space before it) or not, it is still the same word, and it seems desirable to share parameters across all four cases. For example, since "hydroxychloroquine" is a reasonably rare word, it may not have been seen after a quotation mark frequently enough to get its own token. Hence, in that situation it would not only not share its embedding, but it also may be segmented differently from the other cases.

FactoredSegmenter attempts to remedy this problem by representing each (sub)word as a tuple. For example, "hydroxychloroquine" at sentence start would be represented by a tuple
```
{
    lemma = "hydroxychloroquine",
    capitalization = CAP_INITIAL,
    isWordBeginning = WORDBEG_YES,
    isWordEnd = WORDEND_YES
}
```
Each tuple member is called a _factor_. The token identity itself ("hydroxychloroquine") is also represented by a factor, which we call the _lemma_, meaning that it is the base form that may be modified by factors (this is inspired by the linguistic term [lemma](https://simple.wikipedia.org/wiki/Lemma_(linguistics)), which is a base form that gets modified by inflections).

A factor has a type and a value. The lemma is a string. For example, `capitalization` is an enumeration with three values, representing the cases of capitalization of the first letter (beginning of a capitalized word, using the symbol `CAP_INITIAL`), of all letters (all-caps, `CAP_ALL`), or of none of the letters (a regular all-lowercase word, `CAP_NONE`). `isWordBeginning` is conceptually a boolean, but for simplicity, we give each factor a unique data type, so `isWordBeginning` is an enum with two values, `WORDBEG_YES` and `WORDBEG_NO`.

Different lemmas can have different factor sets. For example, digits do not have a capitalization factor. However, for a given lemma, the set of factors is always the same.

When written to a text file, factor tuples are represented by concatenating the factor values, separated by a vertical bar. The above would be `hydroxychloroquine|CAP_INITIAL|WORDBEG_YES|WORDEND_YES`. However, since this can dramatically increase file sizes, factors use short-hand notations when serialized. Also, to make those files a little more readable to humans, lemmas are written in all-caps, while factors use lowercase. The actual form as written to file of the above is:
```
HYDROXYCHLORIQUINE|ci|wb|we
```
For rare words or morphological variant, FactoredSegmenter also supports subword units. A subword unit is used when a word is unseen in the training, or not seen often enough. FactoredSegmenter relies on the SentencePiece library for determining suitable subword units.

For example, the "hydroxychloroquine" might be rare enough to be represented by subwords, such as "hydro" + "xy" + "chloroquine", it would be represented as a sequence of four tuples:
```
{
    lemma = "hydro",
    capitalization = CAP_INITIAL,
    isWordBeginning = WORDBEG_YES,
    isWordEnd = WORDEND_NO
},
{
    lemma = "xy",
    capitalization = CAP_NONE,
    isWordBeginning = WORDBEG_NO,
    isWordEnd = WORDEND_NO
},
{
    lemma = "hydroxychloroquine",
    capitalization = CAP_NONE,
    isWordBeginning = WORDBEG_NO,
    isWordEnd = WORDEND_YES
}
```
The form written to file would be:
```
HYDRO|ci|wb|wen XY|cn|wbn|wen CHLOROQUINE|cn|wbn|we
```
A note on spaces. The tuples above do not encode whether there is a space before or after the word. Instead, it is encoded whether a token is at the boundary (beginning/end) of a word. For single-word tokens, both flags are true. Most of the time, a word boundary implies a spaces, but not always. For example, a word in quotation marks would not be enclosed in spaces; rather the quotation marks would. For example, the sequence "Hydroxychloroquine works" would be encoded as:
```
HYDRO|ci|wb|wen XY|cn|wbn|wen CHLOROQUINE|cn|wbn|we WORKS|cn|wb|we
```
without explicit factors for spaces; rather, the space between "hydroxychloroquine" and "works" is implied by the word-boundary factors.

Hence, words do not carry factors determining space directly. Rather, such factors are carried by punctuation marks. By default, there is always a space between word tokens, and punctuation carries factors stating whether a space surrounding the punctuation should be elided, whether the punctuation should be "glued" to the surrounding token(s). For example, in the sentence "Hydroxychloroquine works!", the exclamation point is glued to the word to the left, and would be represented by the following factor tuple:
```
{
    lemma = "!",
    glueLeft = GLUE_LEFT_YES,
    glueRight = GLUE_RIGHT_NO
}
```
Again, the written form uses short-hands, in this case `gl+` and `gl-` and likewise `gr+` and `gl-`. The full sequence would be encoded as:
```
HYDRO|ci|wb|wen XY|cn|wbn|wen CHLOROQUINE|cn|wbn|we WORKS|cn|wb|we !|gl+|gr-
```
Note that the short-hands for boolean-like factors are a little inconsistent for historical reasons. Note also that this documentation makes no claims regarding the veracity of its example sentences.

An important property of the factor representation is that it allows to fully reconstruct the original input text, it is fully _round-trippable_. If we encode a text as factor tuples, and then decode it, the result will be the original input string. FactoredSegmenter is used in machine translation by training the translation system to translate text in factor representation to text in the target language that is likewise in factor representation. The final surface form is then recreated by decoding factor representation in the target language.

There is one exception to round-trippability. To support specifying specific translations for words ("phrase fixing"), FactoredSegmenter can replace token ranges by special placeholders that get translated as such. Alternatively, it can include the given target translation in the source string, using special factors or marker tags. The identity of such a token would get lost in the factored representation (instead, the translation system would remember its identity as side information).

Lastly, it should be noted that the specific factor sets depend on configuration variables. For example, empirically we found no practical benefit in the `isWordEnd` factor, so this is typically disabled by a configuration setting.

## FactoredSegmenter in Code

FactoredSegmenter is manifested in code in two different ways. First, in the form of a C# library which allows to execute all functions, that is, training, encoding, and decoding. For example, each time a user invokes Microsoft Translator, e.g. via http://translate.bing.com, FactoredSegmenter is invoked via the C# interface.

Secondly, a Linux command-line tool that gives access to most of the library functions. This allows to build offline system using the factored-segmenter tool and Marian alone.

## Training and Factor Configuration

The FactoredSegmenter representation is deterministic, but the subword units are not. Hence, a FactoredSegmenter model must be trained. The training process (which happens transparently) will first pre-tokenize the input into units of consistent letter type, and then execute SentencePiece training on the resulting tokens. The result of the training process are two files:

 * an `.FSM` file, for "factored-segmenter model." An `.FSM` file contains everything needed to encode and decode. It holds all configuration options, the factor specification (which lemma has what factors), subword inventories, and also embeds the binary SentencePiece model for subword splitting.
 * an `.FSV` file, for "factored-segmenter vocabulary." The `.FSV` file holds the subset of the `.FSM` model that is needed by the translation software (Marian) to interpret the factor representation.

At training time, the user must specify all options regarding which factors are used.

*TODO*: To be continued

## Prerequisites

To build FactoredSegmenter, you will need to install the following dependencies:

#### Linux
```
sudo apt-get install dotnet-sdk-3.1
sudo apt-get install dotnet-runtime-3.1
```
And you need to install SentencePiece [from source](https://github.com/google/sentencepiece#c-from-source). SentencePiece is accessed both via executing a binary and via direct invocation of the C++ library.

#### Windows
```
https://dotnet.microsoft.com/download/dotnet-core/thank-you/sdk-3.1.101-windows-x64-installer
```
And SentencePiece. In the Windows version, SentencePiece is presently only invoked via the SentencePiece command-line tools. It has not been tested whether the [vcpkg installation](https://github.com/google/sentencepiece#installation) works.

## How to build

#### Linux
```
cd REPO/src
dotnet publish -c Release -r linux-x64 -f netcoreapp3.1 /p:PublishSingleFile=true /p:PublishTrimmed=true \
  ../factored-segmenter.csproj
# now you can run the binary at REPO/src/bin/Release/netcoreapp3.1/linux-x64/publish/factored-segmenter
```

#### Windows
Open `src` folder in Visual Studio 2017 or later. With 2017, it will complain that it cannot build the 3.1 SDK. F5 debugging still works (using 2.1), but you may need to hit F5 twice.

## Example command lines

### Encoding
```
pigz -d -c /data1/SpeechTrans/ENU-DEU_Student.speech/normalize_src_training_sentences/sentenceonly.src.normalized.ENU.snt.gz \
  | time   parallelized   env LC_ALL=en_US.UTF-8 \
    ~/factored-segmenter/src/bin/Release/netcoreapp3.1/linux-x64/publish/factored-segmenter encode  --model ~/factored-segmenter/enu.deu.generalnn.joint.segmenter.fsm \
  | pigz -c --best \
  > /data1/SpeechTrans/Data/2019-12-ENU-DEU_Student/TN/TrainSingleSent/normalized.ENU.snt.fs.gz
```
### Training
```
time   env LC_ALL=en_US.UTF-8 \
  ~/factored-segmenter/src/bin/Release/netcoreapp3.1/linux-x64/publish/factored-segmenter train \
    --model ~/factored-segmenter/out/enu.deu.generalnn.joint.segmenter.fsm \
    --distinguish-initial-and-internal-pieces  --single-letter-case-factors  --serialize-indices-and-unrepresentables  --inline-fixes \
    --min-piece-count 38  --min-char-count 2  --vocab-size 32000 \
    /data1/SpeechTrans/ENU-DEU_Student.speech/train_segmenter.ENU.DEU.generalnn.joint/corpus.sampled
```
# Contributing

This project welcomes contributions and suggestions.  Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit https://cla.opensource.microsoft.com.

When you submit a pull request, a CLA bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.
