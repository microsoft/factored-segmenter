// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace TextSegmentation.Segmenter.FactoredSegmenter_GitSubmodule.src.Test
{
    using Common.Text;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using System.Diagnostics.CodeAnalysis;

    /// <summary>
    /// Unit tests
    /// </summary>
    [TestClass]
    [ExcludeFromCodeCoverage]
    public class FactoredSegmenterScriptHelperTests
    {
        [TestMethod]
        public void ScriptEdgeCasesTest()
        {
            // We put stuff here to be sure how stuff is classified (e.g. Chinese letter 6 (六) is not considered a number by C#).
            // This is less of a regression test and more of a "documentation" on what we think is true for a few edge cases.
            Assert.IsTrue(Unicode.GetUnicodeMajorDesignation('는') == 'L'); // Korean case markers: make sure they are just like letters
            Assert.IsTrue(Unicode.GetScript('는') == Unicode.Script.Hangul);
            Assert.IsTrue(Unicode.GetUnicodeMajorDesignation('＄') == 'S');
            Assert.IsTrue(Unicode.GetUnicodeMajorDesignation('，') == 'P'); // full-width (incl. Chinese)
            Assert.IsTrue(Unicode.GetUnicodeMajorDesignation('、') == 'P'); // Chinese
            Assert.IsTrue(Unicode.GetUnicodeMajorDesignation('。') == 'P'); // Chinese
            Assert.IsTrue(Unicode.GetUnicodeMajorDesignation('।') == 'P'); // Hindi Danda
            Assert.IsTrue(Unicode.GetUnicodeMajorDesignation('॥') == 'P'); // Hindi Danda
        }

        [TestMethod]
        public void ClassificationEdgeCaseTests()
        {
            // put stuff here to be sure how stuff is classified (e.g. Chinese letter 6 (六) is not considered a number by C#)
            Assert.IsTrue('Ａ'.HasAndIsUpper());
            Assert.IsTrue('Ａ'.IsBicameral());
            Assert.IsTrue(!'ß'.HasAndIsUpper());
            Assert.IsTrue('１'.IsNumeral());
            Assert.IsTrue('〇'.IsNumeral());
            Assert.IsTrue('○'.IsNumeral());     // medium small white circle; is used in Chinese as a zero
            Assert.IsTrue('十'.IsNumeral());
            Assert.IsTrue('六'.IsNumeral());
            Assert.IsTrue('२'.IsNumeral());     // Hindi numeral
            Assert.IsTrue('Ⅹ'.IsNumeral());     // Roman numeral
            Assert.IsTrue('Ⅹ'.HasAndIsUpper()); // Roman numeral--C# IsUpper() gets this wrong
        }
    }
}
