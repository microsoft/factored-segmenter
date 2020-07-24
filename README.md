# FactoredSegmenter

FactoredSegmenter is the unsupervised text tokenizer for machine translation that underlies Microsoft Translator.
It encodes tokens in the form `WORDPIECE|factor1|factor2|...|factorN`.
This encoding syntax is directly understood by the [Marian Neural Machine Translation Toolkit](https://github.com/marian-nmt/marian).
To use FactoredSegmenter with other toolkits, one must implement a parser for this format, and modify the embedding lookup and, to use factors on the target side, the beam decoder.
The term "FactoredSegmenter" refers to both a segmentation library and an encoding of text that aims at _factoring shared properties of words_, such as casing or spacing.

FactoredSegmenter segments words into subwords, or _word pieces_, using the popular [SentencePiece](https://github.com/google/sentencepiece) library under the hood.
However, unlike SentencePiece in its common usage, spaces and capitalization are not encoded in the sub-word tokens themselves.
Instead, spacing and capitalization are encoded in _factors_ that are attached to each token.

The purpose of this is to allow the sharing of model parameters across all occurences of a word, be it
in the middle of a sentence, capitalized at the start of a sentence, at the start of a sentence enclosed in parentheses or quotation marks, or in all-caps in a social-media rant.
In SentencePiece, these are all distinct tokens, which is less robust.
For example, this distinction leads to poor translation accuracy for all-caps sentences, which is problematic when translating social-media posts.

Features:
 * represents words and tokens as _tuples of factors_ to allow for parameter sharing. E.g. spacing and capitalization are separate factors on word pieces;
 * infrequent words are represented by _subwords_, aka word pieces, using the SentencePiece library;
 * robust treatment of _numerals_: Each digit is always its own token, in every writing system. We have observed that this reliably fixes a large class of translation errors for numerals, especially when translating between different numeric systems (such as Arabic numbers to Chinese);
 * support for _"phrase fixing,"_ where specific phrases are required to be translated in a very specific way. Such constrained translation is achieved with FactoredSegmenter by either replacing such phrases by a fixed token (where a factor is used to distinguish multiple such phrase fixes in a single sentence), or by inserting the desired target translation directly into the encoded source, where factors are used to distinguish the source from the target translation;
 * unknown-character handling: characters not covered by the word-piece vocabulary, fore example rare graphical characters, are encoded by their Unicode character code in a form that a translation system can learn to copy through;
 * round-trippable: allows to fully reconstruct the source sentence from the factored (sub)word representation (with minor exceptions);
 * support of continuous scripts, which have different rules for spacing, and combining marks.

## Factors

Let's pick a random word of recent prominence: "hydroxychloroquine." First, observe that whether it occurs at the beginning of the word (where it would normally be capitalized) or within the sentence, or whether it appears after a quotation mark (where it is lower-case but there is no space before it) or not, it is still the same word, and it seems desirable to share parameters across all four cases. For example, since "hydroxychloroquine" is a word rarely seen until recently, it may not have been seen frequently enough after a quotation mark to get its own token. Hence, in that situation it would not only not share its embedding, but it also may be segmented differently from the other cases.

FactoredSegmenter attempts to remedy this problem by representing each (sub)word as a tuple. For example, "hydroxychloroquine" at sentence start would be represented by a tuple
that might be written in pseudo-code as
```
{
    lemma = "hydroxychloroquine",
    capitalization = CAP_INITIAL,
    isWordBeginning = WORDBEG_YES,
    isWordEnd = WORDEND_YES
}
```
Each tuple member is called a _factor_. The subword identity itself ("hydroxychloroquine") is also represented by a factor, which we call the _lemma_, meaning that it is the base form that may be modified by factors (this is inspired by the linguistic term [lemma](https://simple.wikipedia.org/wiki/Lemma_(linguistics)), which is a base form that gets modified by inflections).

A factor has a type and a value. While the lemma is a string, `capitalization` is an enumeration with three values, representing three kinds of capitalization: capitalized first letter (beginning of a capitalized word, using the symbol `CAP_INITIAL`), all-caps (`CAP_ALL`), and no capitalized letters at all (a regular all-lowercase word, `CAP_NONE`). To represent mixed-case words, e.g. RuPaul, we break them into subwords. `isWordBeginning` is conceptually a boolean, but for simplicity, we give each factor a unique data type, so `isWordBeginning` is an enum with two values, `WORDBEG_YES` and `WORDBEG_NO`.

Different lemmas can have different factor sets. For example, digits and punctuation cannot be capitalized,
hence those lemmas not have a capitalization factor. However, for a given lemma, the set of factors is always the same.

For infrequent words or morphological variants, FactoredSegmenter supports subword units. A subword unit is used when a word is unseen in the training, or not seen often enough. FactoredSegmenter relies on the SentencePiece library for determining suitable subword units.

For example, "hydroxychloroquine" might be rare enough to be represented by subwords, such as "hydro" + "xy" + "chloroquine", it would be represented as a sequence of three tuples:
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
    lemma = "chloroquine",
    capitalization = CAP_NONE,
    isWordBeginning = WORDBEG_NO,
    isWordEnd = WORDEND_YES
}
```
The subword nature of the tuples is represented by the `isWordBeginning` and `isWordEnd` factors.

### Factor Syntax

When written to a text file or when communicated to an NMT training toolkit, factor tuples are represented as strings following a specific syntax:
The factor values are concatenated, separated by a vertical bar. A direct concatenation of the above example would give `hydroxychloroquine|CAP_INITIAL|WORDBEG_YES|WORDEND_YES`.
However, to avoid to dramatically increase data-file sizes, factors use short-hand notations when serialized. Also, to make those files a little more readable to humans, lemmas are written in all-caps, while factors use lowercase. If "hydroxychloroquine" is a single word piece, the actual form as written to file of the above is:
```
HYDROXYCHLORIQUINE|ci|wb|we
```
The example above where it is represented by multiple subword units has the following serialized form:
```
HYDRO|ci|wb|wen XY|cn|wbn|wen CHLOROQUINE|cn|wbn|we
```

### Representation of Space Between Tokens

If you are familiar with SentencePiece, you will notice that the tuples above do not directly encode whether there is a space before or after the word. Instead, it is encoded as factors whether a token is at the _boundary_ (beginning/end) of a word. For single-word tokens, both flags are true. Most of the time, a word boundary implies a spaces, but not always. For example, a word in quotation marks would not be enclosed in spaces; rather the quotation marks would. For example, the sequence "Hydroxychloroquine works" would be encoded as:
```
HYDRO|ci|wb|wen XY|cn|wbn|wen CHLOROQUINE|cn|wbn|we WORKS|cn|wb|we
```
without explicit factors for spaces; rather, the space between "hydroxychloroquine" and "works" is implied by the word-boundary factors.

Hence, words do not carry factors determining space directly. Rather, such factors are carried by _punctuation marks_. By default, there is always a space at word boundaries, and punctuation carries factors stating whether a space surrounding the punctuation should rather be _elided_, whether the punctuation should be "glued" to the surrounding token(s). For example, in the sentence "Hydroxychloroquine works!", the exclamation point is glued to the word to the left, and would be represented by the following factor tuple:
```
{
    lemma = "!",
    glueLeft = GLUE_LEFT_YES,
    glueRight = GLUE_RIGHT_NO
}
```
The `glueLeft` factor indicates that the default space after `works` should be elided.
The short-hand form used when writing to file is `gl+` and `gl-` and likewise `gr+` and `gr-`. The full sequence would be encoded as:
```
HYDRO|ci|wb|wen XY|cn|wbn|wen CHLOROQUINE|cn|wbn|we WORKS|cn|wb|we !|gl+|gr-
```
Note that the short-hands for boolean-like factors are a little inconsistent for historical reasons. Note also that this documentation makes no claims regarding the veracity of its example sentences.

### Round-Trippability

An important property of the factor representation is that it allows to fully reconstruct the original input text, it is fully _round-trippable_. If we encode a text as factor tuples, and then decode it, the result will be the original input string. FactoredSegmenter is used in machine translation by training the translation system to translate text in factor representation to text in the target language that is likewise in factor representation. The final surface form is then recreated by decoding factor representation in the target language.

There are few exception to round-trippability. To support specifying specific translations for words ("phrase fixing"), FactoredSegmenter can replace token ranges by special placeholders that get translated as such. Alternatively, it can include the given target translation in the source string, using special factors or marker tags. The identity of such a token would get lost in the factored representation (instead, the translation system would remember its identity as side information). The C# API also allows replacing a character range on the fly (the original characters get lost).

Lastly, it should be noted that the specific factor sets depend on configuration variables. For example, empirically we found no practical benefit in the `isWordEnd` factor, so this is typically disabled by a configuration setting.

## FactoredSegmenter in Code

FactoredSegmenter is manifested in code in two different ways. First, in the form of a C# library which allows to execute all functions, that is, training, encoding, and decoding. For example, each time a user invokes Microsoft Translator, e.g. via http://translate.bing.com, FactoredSegmenter is invoked via the C# interface.

Secondly, a Linux command-line tool that gives access to most of the library functions. This allows to build offline system using the factored-segmenter tool and Marian alone.

## Training and Factor Configuration

The FactoredSegmenter representation is deterministic, but the subword units are not. Hence, a FactoredSegmenter model must be trained. The training process (which happens transparently) will first pre-tokenize the input into units of consistent letter type, and then execute SentencePiece training on the resulting tokens. The result of the training process are two files:

 * an `.FSM` file, for "factored-segmenter model." An `.FSM` file contains everything needed to encode and decode. It holds all configuration options, the factor specification (which lemma has what factors), subword inventories, and also embeds the binary SentencePiece model for subword splitting.
 * an `.FSV` file, for "factored-segmenter vocabulary." The `.FSV` file holds the subset of the `.FSM` model that is needed by the translation software (Marian) to interpret the factor representation.

At training time, the user must specify all options regarding which factors are used.

*TODO*: To be continued, e.g. need to document continuous-script handling, combining marks, some more on numerals; also all model options and command-line arguments

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
