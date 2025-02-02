﻿using System;
using System.Text.RegularExpressions;

namespace Sledge2Resonite;

/// <summary>
/// Does not touch source file, only makes a copy and returns a parsed version
/// </summary>
public abstract class Preprocessor
{
    protected static readonly RegexOptions options 
        = RegexOptions.Multiline | RegexOptions.Compiled;

    protected static readonly Regex replaceComMulti 
        = new Regex(@"\s*//.+?\n", options);
    protected static readonly string replaceComSubMulti = Environment.NewLine;

    protected static readonly Regex suffixReplaceMulti 
        = new Regex(@"(?:""?([^\s""]+)""?[\r\t\f ]+""?([^{/\n\s][^\n]*?)""?\s*\n)", options);
    protected const string suffixReplaceSubMulti = @"""$1"" ""$2""" + "\n";

    protected static readonly Regex sufficReplaceWsMulti 
        = new Regex(@"^\s*$[\r\n]*", options);

    protected static readonly Regex newObjQMarkMulti 
        = new Regex(@"""(.*?)""\s*\n\s*\{", options);
    protected const string objQMarkReplaceSub = "$1\n{";

    protected bool Preprocess(string input) => input.Length != 0;
}