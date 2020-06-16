### factored-segmenter standalone tool

This is a quick-and-dirty version of what will become a proper README in the future.

## Prerequisites:

# Linux:
sudo apt-get install dotnet-sdk-3.1
sudo apt-get install dotnet-runtime-3.1

# Windows
https://dotnet.microsoft.com/download/dotnet-core/thank-you/sdk-3.1.101-windows-x64-installer

## How to build:

# Linux:
cd REPO/src
dotnet publish -c Release -r linux-x64 -f netcoreapp3.1 /p:PublishSingleFile=true /p:PublishTrimmed=true ../factored-segmenter.csproj
# now you can run the binary at REPO/src/bin/Release/netcoreapp3.1/linux-x64/publish/factored-segmenter

# Windows:
Open 'src' folder in VS 2017 or later.
With 2017, it will complain that it cannot build the 3.1 SDK. F5 debugging still works (using 2.1), but you may need to hit F5 twice.

## Example command lines:

# Encoding

pigz -d -c /data1/SpeechTrans/ENU-DEU_Student.speech/normalize_src_training_sentences/sentenceonly.src.normalized.ENU.snt.gz \
  | time   parallelized   env LC_ALL=en_US.UTF-8 \
    ~/factored-segmenter/src/bin/Release/netcoreapp3.1/linux-x64/publish/factored-segmenter encode  --model ~/factored-segmenter/enu.deu.generalnn.joint.segmenter.fsm \
  | pigz -c --best \
  > /data1/SpeechTrans/Data/2019-12-ENU-DEU_Student/TN/TrainSingleSent/normalized.ENU.snt.fs.gz

# Training

time   env LC_ALL=en_US.UTF-8 \
  ~/factored-segmenter/src/bin/Release/netcoreapp3.1/linux-x64/publish/factored-segmenter train \
    --model ~/factored-segmenter/out/enu.deu.generalnn.joint.segmenter.fsm \
    --distinguish-initial-and-internal-pieces  --single-letter-case-factors  --serialize-indices-and-unrepresentables  --inline-fixes \
    --min-piece-count 38  --min-char-count 2  --vocab-size 32000 \
    /data1/SpeechTrans/ENU-DEU_Student.speech/train_segmenter.ENU.DEU.generalnn.joint/corpus.sampled
