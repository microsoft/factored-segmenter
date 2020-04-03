#include <stdio.h>
#include <iostream>
#include <stddef.h>

#include <sentencepiece_processor.h>
#include <memory>  // for unique_ptr
#include <fstream>
#include "unicode_conversions.h"

// This requires libsentencepiece.so. If you follow the build instructions
// from C++ source on https://github.com/google/sentencepiece, the necessary
// header file and library will be installed in the system.

// ---------------------------------------------------------------------------
// C++ implementation of the functionality
// ---------------------------------------------------------------------------

class SentencePieceInterop
{
    std::unique_ptr<sentencepiece::SentencePieceProcessor> m_processor;

    void check_status(sentencepiece::util::Status status, const char* what)
    {
        if (status.ok())
            return;
        std::string err = status.ToString();
        std::cerr << err << std::endl;
        throw std::runtime_error(std::string("SentencePiece error ") + what + ": " + err);
    }
public:
    SentencePieceInterop(const char* model, size_t modelSize, const char** vocab, size_t vocabSize)
    {
        m_processor.reset(new sentencepiece::SentencePieceProcessor());
        // load the model file
        const auto status = m_processor->LoadFromSerializedProto(sentencepiece::util::min_string_view(model, modelSize));
        // implant the restricted vocabulary, if given
        if (vocab && vocabSize > 0)
        {
            std::vector<std::string> vocab_str(vocabSize);
            for (size_t i = 0; i < vocabSize; i++)
                vocab_str[i] = vocab[i];
            m_processor->SetVocabulary(vocab_str);
        }
        check_status(status, "loading");
    }
    size_t EncodeAsIds(const char* wordInUtf8, int* pieceIdBuffer, size_t pieceIdBufferSize)
    {
        auto piece_ids = m_processor->EncodeAsIds(sentencepiece::util::min_string_view(wordInUtf8));
        if (piece_ids.size() > pieceIdBufferSize)
            throw std::out_of_range("EncodeAsIds pieceIdBufferSize is too small");
        std::copy(piece_ids.begin(), piece_ids.end(), pieceIdBuffer);
        return piece_ids.size();
    }
    int UCS2LengthOfPieceId(int pieceId)
    {
        if (m_processor->IsUnknown(pieceId))
            return -1;
        auto utf8 = m_processor->IdToPiece(pieceId);
        return count_utf8_to_utf16(utf8);
    }
};

// ---------------------------------------------------------------------------
// C/C++ interop and exported C functions
//  - intptr_t object = LoadModel(void* model, size_t modelSize, char** vocab, size_t vocabSize)
//  - length = EncodeAsIds(intptr_t object, const char* wordInUtf8, int* pieceIdBuffer, size_t pieceIdBufferSIze)  // pieceIdBuffer size >= strlen(word)+1
//  - n = UCS2LengthOfPieceId(intptr_t object, int pieceId)
//  - UnloadModel(intptr_t object)
// ---------------------------------------------------------------------------

extern "C" {

intptr_t LoadModel(const char* model, size_t modelSize, const char** vocab, size_t vocabSize)
{
    try
    {
        return (intptr_t) new SentencePieceInterop(model, modelSize, vocab, vocabSize);
    }
    catch(...)  // @TODO: how to return meaningful error information?
    {
        return (intptr_t) nullptr;
    }
}

int EncodeAsIds(intptr_t object, const char* wordInUtf8, int* pieceIdBuffer, size_t pieceIdBufferSize)
{
    try
    {
        return (int)((SentencePieceInterop*)object)->EncodeAsIds(wordInUtf8, pieceIdBuffer, pieceIdBufferSize);
    }
    catch(...)  // @TODO: how to return meaningful error information?
    {
        return -1;
    }
}

int UCS2LengthOfPieceId(intptr_t object, int pieceId)
{
    try
    {
        return ((SentencePieceInterop*)object)->UCS2LengthOfPieceId(pieceId);
    }
    catch(...)  // @TODO: how to return meaningful error information?
    {
        return 0;  // 0 is an invalid length
    }
}

void UnloadModel(intptr_t object)
{
    delete (SentencePieceInterop*)object;
}

}

// ---------------------------------------------------------------------------
// BELOW IS MY DEV WRAPPER
// ---------------------------------------------------------------------------

// how to build:
//  - clang -lstdc++ -std=c++11 -lsentencepiece -Wall -Werror SentencePieceInterop.cpp
// how the SPM files for testing were obtained:
//  - run factored-segmenter encode --model /marcinjdeu.blob.core.windows.net/forfrank/model-99995c.fsm
//  - you will see a log msg like this:
//    starting SentencePiece instance as: /usr/local/bin/spm_encode --model /tmp/tmpg9BX8N.tmp --vocabulary /tmp/tmpFslYfv.tmp
//  - copy out the --model and --vocab temp files

using namespace std;

const char* spmModelPath = "/home/fseide/factored-segmenter/spm/spm.model";
const char* spmVocabPath = "/home/fseide/factored-segmenter/spm/spm.vocab";

vector<string> test_strings =
{
    "\u2581HELLO",
    "\u2581OBAMA",
    "OBAMA",
    "HELL\u2582\u2582O"  // out-of-vocab example
};

void fail(const char* msg) { cerr << "FAILED: " << msg << endl; exit(1); }

int main()
{
    // load the model file into RAM
    ifstream f_model(spmModelPath);
    auto modelBytes = vector<char>(istreambuf_iterator<char>(f_model), istreambuf_iterator<char>());
    if (f_model.bad() || modelBytes.empty())  // note: bad bit does not get set if file not found (??)
        fail("Failed to read SPM model file.");

    // load the vocab file
    ifstream f_vocab(spmVocabPath);
    vector<string> vocab;
    while (f_vocab)
    {
        string line;
        getline(f_vocab, line);
        vocab.push_back(line);
    }
    vector<const char*> vocab_ptr;
    for (const auto& line : vocab)
        vocab_ptr.push_back(line.c_str());

    auto object = LoadModel(modelBytes.data(), modelBytes.size(), vocab_ptr.data(), vocab_ptr.size());

    for (const auto& test_string : test_strings)
    {
        cerr << "Testing: " << test_string << endl;
        vector<int> piece_ids(test_string.size() + 1);
        auto num_pieces = EncodeAsIds(object, test_string.c_str(), piece_ids.data(), piece_ids.size());
        if (num_pieces < 0)
            fail("Failed to EncodeAsIds.");
        piece_ids.resize(num_pieces);
        for (auto piece_id : piece_ids)
            cerr << " piece id " << piece_id << " has " << UCS2LengthOfPieceId(object, piece_id) << " UCS-2 characters" << endl;
    }
    UnloadModel(object);
    cerr << "Done." << endl;
}
