// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

// This is meant as an extension of Unicode.cs. It should be merged into there,
// once the code in here has reached a sufficient level of maturity and generality
// across languages, and generally support surrogate pairs.

using System.Collections.Generic;
using System.Linq;

namespace Common.Text
{
    /// <summary>
    /// Helper class for Unicode characters.
    /// @BUGBUG: These do not work with surrogate pairs.
    /// </summary>
    public static class ScriptExtensions
    {
        /// <summary>
        /// Helper to test whether a character has a character code in range min..max
        /// </summary>
        public static bool IsInRange(this char c, int min, int max) => (c >= (char)min && c <= (char)max);

        /// <summary>
        /// Is character a combining character?
        /// </summary>
        /// <param name="c"></param>
        /// <returns></returns>
        public static bool IsCombiner(this char c) => c.GetUnicodeMajorDesignation() == 'M';

        /// <summary>
        /// Is character a Variation Selector? [https://en.wikipedia.org/wiki/Variation_Selectors_(Unicode_block)]
        /// Note that these are included in IsCombiner as well.
        /// </summary>
        //public static bool IsVariationSelector(this char c) => c.IsInRange(0xfe00, 0xfe0f);

        /// <summary>
        /// Helper to determine whether a character is a numeral.
        /// This includes numeral characters that are not classified as such in Unicode,
        /// such as Chinese numbers.
        /// This is meant for FactoredSegmenter, which uses this to prevent numeral characters
        /// from being merged in SentencePiece.
        public static bool IsNumeral(this char c)
        {
            // @BUGBUG: currently known failures:
            //  - Arabic fractions: ٠٫٢٥

            // Chinese numeral letters are not classified as digits in Unicode
            if (ScriptHelpers.ChineseDigits.Contains(c))
                return true;
            else
                return Unicode.GetUnicodeMajorDesignation(c) == 'N';
        }

        /// <summary>
        /// Is this character considered a letter inside FactoredSegmenter?
        /// This also returns true for combiners that are typically used with letters.
        /// @TODO: decide how to handle wide letters, or all sorts of weird letters such as exponents
        ///        Are those letters? Are those capitalizable?
        ///        Then remove this wrapper.
        /// </summary>
        public static bool IsLetterOrLetterCombiner(this char c)
            => char.IsLetter(c) ||
               (c.IsCombiner() && c.GetCombinerTypicalMajorDesignation() == 'L');

        ///// <summary>
        ///// Combine IsLetter() and IsNumeral(), which is used a few times in this combination.
        ///// </summary>
        ///// <param name="c"></param>
        ///// <returns></returns>
        //public static bool IsLetterOrNumeral(this char c) => c.IsLetter() || c.IsNumeral();

        /// <summary>
        /// Tests whether a character is a bicameral letter.
        /// @TODO: should we consider German ess-zet as bicameral? Lower=upper, but
        /// as of recently, an upper-case ess-zet exist.
        /// One can all-caps a word with ess-zet. This is currently special-cased in FactoredSegmenter.
        /// </summary>
        public static bool IsBicameral(this char c) => char.ToLowerInvariant(c) != char.ToUpperInvariant(c);

        /// <summary>
        /// Replacement for IsLower() that handles Roman numeral X correctly
        /// We define a lower-case letter as one that is bicameral in the first place, and of the lower kind.
        /// </summary>
        public static bool HasAndIsLower(this char c) => c != char.ToUpperInvariant(c);
        /// <summary>
        /// Same as HasAndIsLower() except for upper-case.
        /// </summary>
        public static bool HasAndIsUpper(this char c) => char.ToLowerInvariant(c) != c;

        /// <summary>
        /// String/index version of HasAndIsLower().
        /// @BUGBUG: Does not handle surrogate pairs.
        /// </summary>
        public static bool HasAndIsLowerAt(this string s, int index) => s[index].HasAndIsLower();

        /// <summary>
        /// String/index version of HasAndIsUpper().
        /// @BUGBUG: Does not handle surrogate pairs.
        /// </summary>
        public static bool HasAndIsUpperAt(this string s, int index) => s[index].HasAndIsUpper();

        /// <summary>
        /// Test if string is a single Unicode character, with support for surrogate pairs.
        /// Used for detecting unrepresentable Unicode characters.
        /// </summary>
        public static bool IsSingleCharConsideringSurrogatePairs(this string s)
        {
            var length = s.Length;
            return length == 1 ||
                  (length == 2 && char.IsSurrogatePair(s, 0));
        }

        /// <summary>
        /// Capitalize the first letter of a string and return the result.
        /// This function attempts to be efficient and not allocate a new string
        /// if the string is unchanged.
        /// </summary>
        public static string Capitalized(this string s)
        {
            if (!string.IsNullOrEmpty(s) && s.First().HasAndIsLower())
            {
                var a = s.ToArray();
                a[0] = char.ToUpperInvariant(a[0]);
                return new string(a);
            }
            else
                return s;
        }

        /// <summary>
        /// Define a "typical" use for combining marks. FactoredSegmenter requires pieces to
        /// be classifyable as being of word nature or not. Combiners depend on context.
        /// This can lead to a contradiction if a combiner gets separated from its preceding
        /// character by SentencePiece (which we allow since in Hindi, some combiners are morphemes).
        /// The problem is that each lemma has a unique factor set. But if the lemma is a
        /// combiner that is used both with a letter or with punctuation in the corpus,
        /// that lemma ends up with two different factor sets, which is forbidden.
        /// As a 95%-5% solution, we uniquely define a single "typical" use for each combiner.
        /// For example, the accent is considered to always imply a letter, although I have
        /// seen it used on top of a space character (to mimic an apostrophe). We consider
        /// these as abnormal uses, which will just lead to an additional forced word break
        /// that can still be learned and resolved by the MT model itself.
        /// </summary>
        public static char GetCombinerTypicalMajorDesignation(this char c)
        {
            // @TODO: Spencer pointed out that the key-cap combiner combines with 0..9, #, and *
            //        It probably should be considered punctuation, to avoid # key-cap A to form a word "key-cap A".
            if (c.IsInRange(0xfe0e, 0xfe0f)) // Variation Selectors 15 and 16 apply to Emojis
                return 'P'; // punctuation
            else
                return 'L'; // letter
        }

        /// <summary>
        /// Classify a character, using our special rules
        ///  - number letters, e.g. Chinese numerals, are classified as 'N'
        ///  - combiners have a single "typical" designation
        /// </summary>
        public static char GetUnicodeMajorDesignationWithOurSpecialRules(this char c) // helper to get character designation, with our special rules for numerals and combiners
        {
            if (c.IsNumeral())
                return 'N';
            var d = c.GetUnicodeMajorDesignation();
            if (d == 'M')
                return c.GetCombinerTypicalMajorDesignation();
            else
                return d;
        }

        /// <summary>
        /// Get the major unicode designation at a character position.
        /// In the special case that that position is a combiner, find the first non-combining
        /// character and use its designation.
        /// </summary>
        //public static char GetUnicodeMajorDesignationBeforeCombinerAt(this string s, int pos)
        //{
        //    var majorDesignation = s[pos].GetUnicodeMajorDesignation();
        //    // if combiner then search for base char (=last non-combining char)
        //    while (majorDesignation == 'M' && pos --> 0)
        //        majorDesignation = s[pos].GetUnicodeMajorDesignation();
        //    return majorDesignation;
        //}
    }

    /// <summary>
    /// Character-script (as in writing-system) related helpers for FactoredSegmenter. These helpers are at present
    /// not yet generic or mature enough to warrant being moved into Common or Unicode.cs.
    /// Once they are, they should be moved.
    /// </summary>
    public static class ScriptHelpers
    {
        public static HashSet<char> ChineseDigits = new HashSet<char>{
            // cf https://en.wikipedia.org/wiki/Chinese_numerals
            '〇', '一', '二', '三', '四', '五', '六', '七', '八', '九', // base digits
            '０', '１', '２', '３', '４', '５', '６', '７', '８', '９', // full-width digits. Note: These are designated as digits
            '十', '百', '千', '萬', '万', '億', '亿', '兆', // units
            '零', '壹', '貳', '贰', '叄', '叁', '陸', '陆', '柒', '捌', '玖', '拾', '佰', '仟', // financial
            '幺', '兩', '两', '倆', '仨', '呀', '念', '廿', '卅', '卌', '皕', // regional
            // @TODO: how about fractions? E.g. 分 (fen)
            '○' // Small White Circle" (U+25CB)
            // It is commonly used as zero in Chinese, but technically not a numeral. Unicode desig is Other Symbol "So".
            // @BUGBUG: For now, we treat it as one since all we care is that it does not get merged.
            //          @TODO: Decide whether we can add a different category that also never gets merged.
            // @TODO:
            //  京 = 10^16 
            //  壱 = formal 1
            //  弐 = formal 2
            //  参 = formal 3 (has other uses)
            // @REVIEW: A native speaker of Chinese and Japanese should check whether some characters
            //          above are commonly used in regular words as well, and assess whether we need them here.
        };

        /// <summary>
        /// Get script designators for each character in a line.
        /// This function handles surrogate pairs and combining marks.   --@TODO: ...not yet, actually
        /// The function can optionally operate on a substring.
        /// </summary>
        //public static Unicode.Script[] GetScripts(string line, int startIndex = 0, int length = int.MaxValue)
        //{
        //    if (length == int.MaxValue)
        //        length = line.Length;
        //    var scripts = new Unicode.Script[length];
        //    for (var i = 0; i < length; i++)
        //    {
        //        // @TODO: Handle surrogates
        //        char c = line[startIndex + i];
        //        if (c.IsCombiner() && i > 0)
        //            scripts[i] = scripts[i - 1];
        //        else
        //            scripts[i] = Unicode.GetScript(c);
        //    }
        //    return scripts;
        //}

        /// <summary>
        /// Simplistic word-boundary detector.
        /// This function attempts to detect word boundaries that can be detected in a language-independent
        /// fashion from the surface form, and without additional knowledge sources.
        /// I.e. it looks for a change in script and some changes in Unicode character designation.
        /// This does not detect word breaks in continuous scripts, which require additional knowledge sources.
        /// This function handles these special cases:
        ///  - some known allowed punctuation between characters, such as ' in words and . in numbers
        ///    @TODO: This rule may not apply to all scripts.
        ///  - surrogate pairs    --@TODO
        ///  - combiners inherit the script of the character to the left
        ///  - combiners are classified as the char type (major designation) they are "typically"
        ///    applied to (not depending on actual char).
        ///    This is needed so that combiners that end up as single SentencePieces are classifyable.
        ///    Any error this causes must be learned by the model.
        ///  - designation changes only are a boundary if a letter or a number is on either side,
        ///    but e.g. not a punctuation symbol next to a space or math symbol
        ///  - (special rule: Hiragana is not split from Kanji. Currently this rule is disabled.)
        /// Each space gives rise to two boundaries (one on each side).
        /// It returns a cut list. An empty string is not cut.
        /// </summary>
        public static IList<int> DetectUnambiguousWordBreaks(string line) // @TODO: Better name for this?
        {
            // First, determines the major Unicode designation and script for each character, but with modifications,
            // for purpose of simple word breaking:
            //  - allowed punctuation marks inside words are flipped to 'L'
            //  - allowed punctuation marks inside numbers are flipped to 'N'
            //  - unambiguous CJK number letters are flipped to 'N'
            //  - combining marks carry over both designation and script from their main character
            var scripts = new Unicode.Script[line.Length];
            var designations = new char[line.Length];
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                // @TODO: handle surrogate pairs
                var m = Unicode.GetUnicodeMajorDesignation(c);
                var s = Unicode.GetScript(c);
                // special case: consider unambiguous CJK number symbols as numerals
                if (c.IsNumeral())
                    designations[i] = 'N';
                // special case: combining marks carry over main character's script, and are classified as their most likely use (for consistency)
                else if (m == 'M')
                {
                    m = c.GetCombinerTypicalMajorDesignation();
                    if (i > 0)
                        s = scripts[i - 1];
                }
                designations[i] = m;
                scripts[i] = s;
                // special case: allowed punctuation inside a word  --@TODO: Likely script dependent, maybe language dependent
                if (i - 2 >= 0 && designations[i] == 'L' && designations[i - 2] == 'L' && IsValidPuncInsideWord(line[i - 1]))
                    designations[i - 1] = 'L';
                // special case: allowed punctuation inside a number  --@TODO: Likely script dependent, maybe language-locale dependent
                else if (i - 2 >= 0 && designations[i] == 'N' && designations[i - 2] == 'N' && IsValidPuncInsideNumber(line[i - 1]))
                    designations[i - 1] = 'N';
                // @TODO: double-check handling of space characters: non-breaking space; optional hyphen
            }

            // This function operates on a string, so we can handle the case of Unicode.Script 1 - Common - Unicode.Script 2
            // This presently breaks this as (Unicode.Script 1 - Common, Unicode.Script 2).
            // Without further knowledge, we can only make an arbitrary hard choice here.
            // This is used by FactoredSegmenter, where that is OK because characters in Common are
            // typically broken off anyways.

            if (line.Length == 0) // graceful exit in case of empty input
                return new List<int>{ 0, 0 }; // empty input is not cut

            var cutList = new List<int>(200) { 0 }; // (0=line start, which the resulting cut list must include)
            var lastNonCommonScript = scripts[0];
            //if (lastNonCommonScript == Unicode.Script.Hiragana)
            //    lastNonCommonScript = Unicode.Script.Han; // no boundary between Kanji and Hiragana
            for (var pos = 1; pos < line.Length; pos++)
            {
                // detect change in character designation
                //  - break at number boundaries
                //     - add number factor
                //     - can numbers be part of words that need to be kept together for determining word-level factors?
                //  - break at word boundaries
                //     - letter/non-letter transitions
                //     - don't break apostrophes and hyphens with letters on both sides
                //     - break at script boundaries
                bool atDesignationChange = (designations[pos - 1] != designations[pos] &&
                                            (designations[pos - 1] == 'N' || designations[pos] == 'N' ||
                                             designations[pos - 1] == 'L' || designations[pos] == 'L'));

                // detect script change
                var thisScript = scripts[pos];
                //if (thisScript == Unicode.Script.Hiragana) // the jury is still out whether we should do this or not
                //    thisScript = Unicode.Script.Han;
                bool atScriptChange = lastNonCommonScript != thisScript && thisScript != Unicode.Script.Common;
                // Note: If there is a script change across Common, we choose one arbitrarily.
                if (thisScript != Unicode.Script.Common || atDesignationChange) // condition 'atDesignationChange' is for back compat only; maybe not needed
                    lastNonCommonScript = thisScript;

                // add cut point if one was found
                if (atDesignationChange || atScriptChange)
                    cutList.Add(pos);
            }
            cutList.Add(line.Length);
            return cutList;
        }
        // @TODO: These next two functions should likely be script-dependent (and possibly language-dependent).
        static bool IsValidPuncInsideWord(char c) => (c == '\'' || c == '-' || c == '\u00AD'/*soft hyphen*/); // true if words may contain this punctuation symbol inside, e.g. "It's", "well-behaved"
        static bool IsValidPuncInsideNumber(char c) => (c == '.' || c == ',' || c == '\u2009'/*thin space*/); // true if numbers may contain this punctuation symbol inside, e.g. "1,234.56"
    }
}
