# CMakeList.txt : CMake project for SentencePieceWrapper, include source and define
# project specific logic here.
#
cmake_minimum_required (VERSION 3.8)

# Add source to this project's executable.
add_executable (SentencePieceInterop "SentencePieceInterop.cpp" "unicode_conversions.h")

# TODO: Add tests and install targets if needed.
