// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Common.Text
{
    /// <summary>Helper functions for Unicode</summary>
    public static class Unicode
    {
        /// <summary>
        /// Translate the UnicodeCategory into the two-letter Unicode-designation representation
        /// </summary>
        public static string GetUnicodeDesignation(this char c)
        {
            // derived from UnicodeCategory enum, which has these strings in the comment
            switch (CharUnicodeInfo.GetUnicodeCategory(c))
            {
                case UnicodeCategory.UppercaseLetter:         return "Lu"; // (letter, uppercase)
                case UnicodeCategory.LowercaseLetter:         return "Ll"; // (letter, lowercase)
                case UnicodeCategory.TitlecaseLetter:         return "Lt"; // (letter, titlecase)
                case UnicodeCategory.ModifierLetter:          return "Lm"; // (letter, modifier)
                case UnicodeCategory.OtherLetter:             return "Lo"; // (letter, other)
                case UnicodeCategory.NonSpacingMark:          return "Mn"; // (mark, nonspacing) combined with another and so not consuming additional horizontal space
                case UnicodeCategory.SpacingCombiningMark:    return "Mc"; // (mark, spacing combining)
                case UnicodeCategory.EnclosingMark:           return "Me"; // (mark, enclosing)
                case UnicodeCategory.DecimalDigitNumber:      return "Nd"; // (number, decimal digit)
                case UnicodeCategory.LetterNumber:            return "Nl"; // (number, letter)
                case UnicodeCategory.OtherNumber:             return "No"; // (number other)
                case UnicodeCategory.SpaceSeparator:          return "Zs"; // (separator, space)
                case UnicodeCategory.LineSeparator:           return "Zl"; // (separator, line)
                case UnicodeCategory.ParagraphSeparator:      return "Zp"; // (separator, paragraph)
                case UnicodeCategory.Control:                 return "Cc"; // (other, control)
                case UnicodeCategory.Format:                  return "Cf"; // (other format)
                case UnicodeCategory.Surrogate:               return "Cs"; // (other surrogate)
                case UnicodeCategory.PrivateUse:              return "Co"; // (other, private use)
                case UnicodeCategory.ConnectorPunctuation:    return "Pc"; // (punctuation, connector)
                case UnicodeCategory.DashPunctuation:         return "Pd"; // (punctuation dash)
                case UnicodeCategory.OpenPunctuation:         return "Ps"; // (punctuation open)
                case UnicodeCategory.ClosePunctuation:        return "Pe"; // (punctuation close)
                case UnicodeCategory.InitialQuotePunctuation: return "Pi"; // (punctuation, initial quote)
                case UnicodeCategory.FinalQuotePunctuation:   return "Pf"; // (punctuation, final quote)
                case UnicodeCategory.OtherPunctuation:        return "Po"; // (punctuation, other)
                case UnicodeCategory.MathSymbol:              return "Sm"; // (symbol, math)
                case UnicodeCategory.CurrencySymbol:          return "Sc"; // (symbol currency)
                case UnicodeCategory.ModifierSymbol:          return "Sk"; // (symbol, modifier)
                case UnicodeCategory.OtherSymbol:             return "So"; // (symbol, other)
                case UnicodeCategory.OtherNotAssigned:        return "Cn"; // (other, not assigned)
                default: throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        /// Translate the UnicodeCategory into the one-letter major Unicode-designation representation
        /// @TODO: Find a way to handle surrogate pairs
        /// </summary>
        public static char GetUnicodeMajorDesignation(this char c) => GetUnicodeDesignation(c)[0];

        /// <summary>
        /// Test whether a script is continuous (not written with spaces).
        /// This informs FactoredSegmenter which factor to use for word/segment boundaries,
        /// which affects which rules the system learns regarding inserting spaces.
        /// </summary>
        public static bool IsContinuousScript(this char c)
        {
            var script = GetScript(c);
            return script == Script.Han ||
                   script == Script.Hiragana || script == Script.Katakana ||
                   script == Script.Thai;
        }

        /// <summary>Names of Unicode Scripts.  Scripts are set of chars like Arabic, Latin, Cyrillic, etc</summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1034:NestedTypesShouldNotBeVisible"), System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1028:EnumStorageShouldBeInt32", Justification = "Use Byte for Enum to optimize storage of 65Kb array")]
        public enum Script : byte
        {
            None,      // not a script (i.e. "null" script value)
            Arabic,
            Common,    // commonly used in more than one script
            Cyrillic,
            Devanagari,
            Greek,
            Han,
            Hangul,
            Hebrew,
            Hiragana,
            Inherited, // considered to have the same script as that of the preceding character
            Katakana,
            Latin,
            Thai,
            Unknown    // valid Unicode codepoint with unknown script (per Unicode spec)
        }
        /// <summary>
        /// Returns the <see cref="Script"/> value for the given Unicode code point
        /// </summary>
        /// <param name="chUtf32">The Unicode code point of the desired character</param>
        /// <returns>The <see cref="Script"/> value for the given Unicode code point</returns>
        private static Script GetValue(int chUtf32)
        {
            if (chUtf32 < s_ScriptByChar.Length)
                return s_ScriptByChar[chUtf32];
            else
                return Script.Unknown;
        }
        /// <summary>Returns a script of a given character</summary>
        /// <param name="value">The character to check</param>
        /// <returns>The <see cref="Script"/> value for the given character</returns>
        public static Script GetScript(char value)
        {
            // @BUGBUG: This interface is flawed. We must handle surrogate pairs correctly.
            return !char.IsSurrogate(value) ?
                GetValue(value) : Script.None;
        }

        private static Script[] s_ScriptByChar; // [unicode code point] -> Script
        static Unicode()
        {
            // initialize the static script-mapping table
            // process for getting this (only doing this once, so not writing a script for it):
            //  - run MTMAIN Common.Text.Unicode.Scripts.Create() and get result of GetScriptRanges().
            //  - replace Name= by Name=Script., replace DynamicValueStart by a number
            //  - format as a 3-column table, e.g. "0	31	Script.Common"
            //  - write to a file
            //  - delete all numeric scripts, collate consecutive ranges
            //    grep -v 'Script.[0-9]' d:\me\x1 | sort -n | gawk "{if ($1==P2+1 && P3==$3) {P2=$2} else {print P1, P2, P3; P1=$1;P2=$2;P3=$3}}END{print P1, P2, P3}" | grep Script | gawk '{if (NR%4 == 0) {print ""}; printf("(%i,%i,%s), ", $1, $2, $3)}' | clip
            var ranges = new (int min, int max, Script script)[]
            {
                (0,64,Script.Common), (65,90,Script.Latin), (91,96,Script.Common),
                (97,122,Script.Latin), (123,169,Script.Common), (170,170,Script.Latin), (171,185,Script.Common),
                (186,186,Script.Latin), (187,191,Script.Common), (192,214,Script.Latin), (215,215,Script.Common),
                (216,246,Script.Latin), (247,247,Script.Common), (248,696,Script.Latin), (697,735,Script.Common),
                (736,740,Script.Latin), (741,745,Script.Common), (748,767,Script.Common), (768,879,Script.Inherited),
                (880,883,Script.Greek), (884,884,Script.Common), (885,887,Script.Greek), (890,893,Script.Greek),
                (894,894,Script.Common), (900,900,Script.Greek), (901,901,Script.Common), (902,902,Script.Greek),
                (903,903,Script.Common), (904,906,Script.Greek), (908,908,Script.Greek), (910,929,Script.Greek),
                (931,993,Script.Greek), (1008,1023,Script.Greek), (1024,1156,Script.Cyrillic), (1157,1158,Script.Inherited),
                (1159,1319,Script.Cyrillic), (1417,1417,Script.Common), (1425,1479,Script.Hebrew), (1488,1514,Script.Hebrew),
                (1520,1524,Script.Hebrew), (1536,1540,Script.Arabic), (1542,1547,Script.Arabic), (1548,1548,Script.Common),
                (1549,1562,Script.Arabic), (1563,1563,Script.Common), (1566,1566,Script.Arabic), (1567,1567,Script.Common),
                (1568,1599,Script.Arabic), (1600,1600,Script.Common), (1601,1610,Script.Arabic), (1611,1621,Script.Inherited),
                (1622,1631,Script.Arabic), (1632,1641,Script.Common), (1642,1647,Script.Arabic), (1648,1648,Script.Inherited),
                (1649,1756,Script.Arabic), (1757,1757,Script.Common), (1758,1791,Script.Arabic), (1872,1919,Script.Arabic),
                (2208,2208,Script.Arabic), (2210,2220,Script.Arabic), (2276,2302,Script.Arabic), (2304,2384,Script.Devanagari),
                (2385,2386,Script.Inherited), (2387,2403,Script.Devanagari), (2404,2405,Script.Common), (2406,2423,Script.Devanagari),
                (2425,2431,Script.Devanagari), (3585,3642,Script.Thai), (3647,3647,Script.Common), (3648,3675,Script.Thai),
                (4053,4056,Script.Common), (4347,4347,Script.Common), (4352,4607,Script.Hangul), (5867,5869,Script.Common),
                (5941,5942,Script.Common), (6146,6147,Script.Common), (6149,6149,Script.Common), (7376,7378,Script.Inherited),
                (7379,7379,Script.Common), (7380,7392,Script.Inherited), (7393,7393,Script.Common), (7394,7400,Script.Inherited),
                (7401,7404,Script.Common), (7405,7405,Script.Inherited), (7406,7411,Script.Common), (7412,7412,Script.Inherited),
                (7413,7414,Script.Common), (7424,7461,Script.Latin), (7462,7466,Script.Greek), (7467,7467,Script.Cyrillic),
                (7468,7516,Script.Latin), (7517,7521,Script.Greek), (7522,7525,Script.Latin), (7526,7530,Script.Greek),
                (7531,7543,Script.Latin), (7544,7544,Script.Cyrillic), (7545,7614,Script.Latin), (7615,7615,Script.Greek),
                (7616,7654,Script.Inherited), (7676,7679,Script.Inherited), (7680,7935,Script.Latin), (7936,7957,Script.Greek),
                (7960,7965,Script.Greek), (7968,8005,Script.Greek), (8008,8013,Script.Greek), (8016,8023,Script.Greek),
                (8025,8025,Script.Greek), (8027,8027,Script.Greek), (8029,8029,Script.Greek), (8031,8061,Script.Greek),
                (8064,8116,Script.Greek), (8118,8132,Script.Greek), (8134,8147,Script.Greek), (8150,8155,Script.Greek),
                (8157,8175,Script.Greek), (8178,8180,Script.Greek), (8182,8190,Script.Greek), (8192,8203,Script.Common),
                (8204,8205,Script.Inherited), (8206,8292,Script.Common), (8298,8304,Script.Common), (8305,8305,Script.Latin),
                (8308,8318,Script.Common), (8319,8319,Script.Latin), (8320,8334,Script.Common), (8336,8348,Script.Latin),
                (8352,8378,Script.Common), (8400,8432,Script.Inherited), (8448,8485,Script.Common), (8486,8486,Script.Greek),
                (8487,8489,Script.Common), (8490,8491,Script.Latin), (8492,8497,Script.Common), (8498,8498,Script.Latin),
                (8499,8525,Script.Common), (8526,8526,Script.Latin), (8527,8543,Script.Common), (8544,8584,Script.Latin),
                (8585,8585,Script.Common), (8592,9203,Script.Common), (9216,9254,Script.Common), (9280,9290,Script.Common),
                (9312,9983,Script.Common), (9985,10239,Script.Common), (10496,11084,Script.Common), (11088,11097,Script.Common),
                (11360,11391,Script.Latin), (11744,11775,Script.Cyrillic), (11776,11835,Script.Common), (11904,11929,Script.Han),
                (11931,12019,Script.Han), (12032,12245,Script.Han), (12272,12283,Script.Common), (12288,12292,Script.Common),
                (12293,12293,Script.Han), (12294,12294,Script.Common), (12295,12295,Script.Han), (12296,12320,Script.Common),
                (12321,12329,Script.Han), (12330,12333,Script.Inherited), (12334,12335,Script.Hangul), (12336,12343,Script.Common),
                (12344,12347,Script.Han), (12348,12351,Script.Common), (12353,12438,Script.Hiragana), (12441,12442,Script.Inherited),
                (12443,12444,Script.Common), (12445,12447,Script.Hiragana), (12448,12448,Script.Common), (12449,12538,Script.Katakana),
                (12539,12540,Script.Common), (12541,12543,Script.Katakana), (12593,12686,Script.Hangul), (12688,12703,Script.Common),
                (12736,12771,Script.Common), (12784,12799,Script.Katakana), (12800,12830,Script.Hangul), (12832,12895,Script.Common),
                (12896,12926,Script.Hangul), (12927,13007,Script.Common), (13008,13054,Script.Katakana), (13056,13143,Script.Katakana),
                (13144,13311,Script.Common), (13312,19893,Script.Han), (19904,19967,Script.Common), (19968,40908,Script.Han),
                (42560,42647,Script.Cyrillic), (42655,42655,Script.Cyrillic), (42752,42785,Script.Common), (42786,42887,Script.Latin),
                (42888,42890,Script.Common), (42891,42894,Script.Latin), (42896,42899,Script.Latin), (42912,42922,Script.Latin),
                (43000,43007,Script.Latin), (43056,43065,Script.Common), (43232,43259,Script.Devanagari), (43360,43388,Script.Hangul),
                (44032,55203,Script.Hangul), (55216,55238,Script.Hangul), (55243,55291,Script.Hangul), (63744,64109,Script.Han),
                (64112,64217,Script.Han), (64256,64262,Script.Latin), (64285,64310,Script.Hebrew), (64312,64316,Script.Hebrew),
                (64318,64318,Script.Hebrew), (64320,64321,Script.Hebrew), (64323,64324,Script.Hebrew), (64326,64335,Script.Hebrew),
                (64336,64449,Script.Arabic), (64467,64829,Script.Arabic), (64830,64831,Script.Common), (64848,64911,Script.Arabic),
                (64914,64967,Script.Arabic), (65008,65020,Script.Arabic), (65021,65021,Script.Common), (65024,65039,Script.Inherited),
                (65040,65049,Script.Common), (65056,65062,Script.Inherited), (65072,65106,Script.Common), (65108,65126,Script.Common),
                (65128,65131,Script.Common), (65136,65140,Script.Arabic), (65142,65276,Script.Arabic), (65279,65279,Script.Common),
                (65281,65312,Script.Common), (65313,65338,Script.Latin), (65339,65344,Script.Common), (65345,65370,Script.Latin),
                (65371,65381,Script.Common), (65382,65391,Script.Katakana), (65392,65392,Script.Common), (65393,65437,Script.Katakana),
                (65438,65439,Script.Common), (65440,65470,Script.Hangul), (65474,65479,Script.Hangul), (65482,65487,Script.Hangul),
                (65490,65495,Script.Hangul), (65498,65500,Script.Hangul), (65504,65510,Script.Common), (65512,65518,Script.Common),
                (65529,65533,Script.Common), (65792,65794,Script.Common), (65799,65843,Script.Common), (65847,65855,Script.Common),
                (65856,65930,Script.Greek), (65936,65947,Script.Common), (66000,66044,Script.Common), (66045,66045,Script.Inherited),
                (69216,69246,Script.Arabic), (110592,110592,Script.Katakana), (110593,110593,Script.Hiragana), (118784,119029,Script.Common),
                (119040,119078,Script.Common), (119081,119142,Script.Common), (119143,119145,Script.Inherited), (119146,119162,Script.Common),
                (119163,119170,Script.Inherited), (119171,119172,Script.Common), (119173,119179,Script.Inherited), (119180,119209,Script.Common),
                (119210,119213,Script.Inherited), (119214,119261,Script.Common), (119296,119365,Script.Greek), (119552,119638,Script.Common),
                (119648,119665,Script.Common), (119808,119892,Script.Common), (119894,119964,Script.Common), (119966,119967,Script.Common),
                (119970,119970,Script.Common), (119973,119974,Script.Common), (119977,119980,Script.Common), (119982,119993,Script.Common),
                (119995,119995,Script.Common), (119997,120003,Script.Common), (120005,120069,Script.Common), (120071,120074,Script.Common),
                (120077,120084,Script.Common), (120086,120092,Script.Common), (120094,120121,Script.Common), (120123,120126,Script.Common),
                (120128,120132,Script.Common), (120134,120134,Script.Common), (120138,120144,Script.Common), (120146,120485,Script.Common),
                (120488,120779,Script.Common), (120782,120831,Script.Common), (126464,126467,Script.Arabic), (126469,126495,Script.Arabic),
                (126497,126498,Script.Arabic), (126500,126500,Script.Arabic), (126503,126503,Script.Arabic), (126505,126514,Script.Arabic),
                (126516,126519,Script.Arabic), (126521,126521,Script.Arabic), (126523,126523,Script.Arabic), (126530,126530,Script.Arabic),
                (126535,126535,Script.Arabic), (126537,126537,Script.Arabic), (126539,126539,Script.Arabic), (126541,126543,Script.Arabic),
                (126545,126546,Script.Arabic), (126548,126548,Script.Arabic), (126551,126551,Script.Arabic), (126553,126553,Script.Arabic),
                (126555,126555,Script.Arabic), (126557,126557,Script.Arabic), (126559,126559,Script.Arabic), (126561,126562,Script.Arabic),
                (126564,126564,Script.Arabic), (126567,126570,Script.Arabic), (126572,126578,Script.Arabic), (126580,126583,Script.Arabic),
                (126585,126588,Script.Arabic), (126590,126590,Script.Arabic), (126592,126601,Script.Arabic), (126603,126619,Script.Arabic),
                (126625,126627,Script.Arabic), (126629,126633,Script.Arabic), (126635,126651,Script.Arabic), (126704,126705,Script.Arabic),
                (126976,127019,Script.Common), (127024,127123,Script.Common), (127136,127150,Script.Common), (127153,127166,Script.Common),
                (127169,127183,Script.Common), (127185,127199,Script.Common), (127232,127242,Script.Common), (127248,127278,Script.Common),
                (127280,127339,Script.Common), (127344,127386,Script.Common), (127462,127487,Script.Common), (127488,127488,Script.Hiragana),
                (127489,127490,Script.Common), (127504,127546,Script.Common), (127552,127560,Script.Common), (127568,127569,Script.Common),
                (127744,127776,Script.Common), (127792,127797,Script.Common), (127799,127868,Script.Common), (127872,127891,Script.Common),
                (127904,127940,Script.Common), (127942,127946,Script.Common), (127968,127984,Script.Common), (128000,128062,Script.Common),
                (128064,128064,Script.Common), (128066,128247,Script.Common), (128249,128252,Script.Common), (128256,128317,Script.Common),
                (128320,128323,Script.Common), (128336,128359,Script.Common), (128507,128576,Script.Common), (128581,128591,Script.Common),
                (128640,128709,Script.Common), (128768,128883,Script.Common), (131072,173782,Script.Han), (173824,177972,Script.Han),
                (177984,178205,Script.Han), (194560,195101,Script.Han), (917505,917505,Script.Common), (917536,917631,Script.Common),
                (917760,917999,Script.Inherited)
            };
            s_ScriptByChar = Enumerable.Repeat(Script.Unknown, ranges.Last().max + 1).ToArray();
            foreach (var range in ranges)
                for (int i = range.min; i <= range.max; i++)
                    s_ScriptByChar[i] = range.script;
        }
    }
}
