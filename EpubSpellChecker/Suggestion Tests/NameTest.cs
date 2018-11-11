using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.ComponentModel;

namespace EpubSpellChecker
{
    [Description("Checks if the given word is a name.")]
    [DisplayName("Names")]
    class NameTest : ITest
    {

        /// <summary>
        /// Checks if the given word is a name.
        /// A name starts with a capital, has proper casing and is also used in the middle of a sentence.
        /// </summary>
        /// <param name="we">The word entry to test</param>
        public static void Test(WordEntry we)
        {
            if (we.IsUnknownWord && !we.Ignore)
            {
                // all occurences of word start with capital and use the proper case (not all upper)
                // and the word is not only used at the start of a sentence
                var textWithProperCase = we.Text.ProperCase();
                if (we.Occurrences.All(occ => occ.Text.StartsWithCapital() && occ.Text == textWithProperCase) && !we.Occurrences.All(occ => occ.IsStartOfSentence))
                {
                    we.UnknownType = "Possible name?";
                    we.Ignore = true;
                }
            }
        }


    }
}
