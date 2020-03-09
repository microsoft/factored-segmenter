This directory contains support libraries that are used by the main
FactoredSegmenter sources in the src/ directory.

The standalone command-line tool build uses these libraries here.

The production build uses a different version of this library, which is
included in our production environment, and is proprietary. The files in this
directory contain a subset of those production libraries that implements only
those classes and methods that are used by the standalone build, sometimes in
greatly simplified versions.
