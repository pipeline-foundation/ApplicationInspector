﻿// Copyright (C) Microsoft. All rights reserved. Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Xml;
using System.Xml.XPath;
using gfs.YamlDotNet.YamlPath;
using JsonCons.JsonPath;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YamlDotNet.RepresentationModel;

namespace Microsoft.ApplicationInspector.RulesEngine;

/// <summary>
///     Class to handle text as a searchable container
/// </summary>
public class TextContainer
{
    private readonly ILogger _logger;

    private readonly string inline;
    private readonly string prefix;
    private readonly string suffix;

    private bool _triedToConstructJsonDocument;
    private JsonDocument? _jsonDocument;
    private object _jsonLock = new();

    private bool _triedToConstructXPathDocument;
    private XPathDocument? _xmlDoc;
    private object _xpathLock = new();

    private bool _triedToConstructYmlDocument;
    private YamlStream? _ymlDocument;
    private object _yamlLock = new();
    private readonly string? _filePath;

    /// <summary>
    ///     Creates new instance
    /// </summary>
    /// <param name="content"> Text to work with </param>
    /// <param name="language"> The language of the test </param>
    /// <param name="languages">
    ///     An instance of the <see cref="Languages" /> class containing the information for language
    ///     mapping to use.
    /// </param>
    /// <param name="loggerFactory">An optional logger factory to receive error messages</param>
    /// <param name="filePath">A string which represents the location of the file the contents of this container contains used to enrich error messages to the <paramref name="loggerFactory"/></param>
    public TextContainer(string content, string language, Languages languages, ILoggerFactory? loggerFactory = null, string? filePath = null)
    {
        _filePath = filePath;
        _logger = loggerFactory?.CreateLogger<TextContainer>() ?? NullLogger<TextContainer>.Instance;
        Language = language;
        FullContent = content;
        LineEnds = new List<int> { 0 };
        LineStarts = new List<int> { 0, 0 };

        // Find line end in the text
        var pos = FullContent.IndexOf('\n');
        while (pos > -1)
        {
            LineEnds.Add(pos);

            if (pos + 1 < FullContent.Length)
            {
                LineStarts.Add(pos + 1);
            }

            pos = FullContent.IndexOf('\n', pos + 1);
        }

        if (LineEnds.Count < LineStarts.Count)
        {
            LineEnds.Add(FullContent.Length - 1);
        }

        prefix = languages.GetCommentPrefix(Language);
        suffix = languages.GetCommentSuffix(Language);
        inline = languages.GetCommentInline(Language);
        Languages = languages;
    }

    public Languages Languages { get; set; }

    /// <summary>
    ///     The full string of the TextContainer represents.
    /// </summary>
    public string FullContent { get; }

    /// <summary>
    ///     The code language of the file
    /// </summary>
    public string Language { get; }

    /// <summary>
    ///     One-indexed array of the character indexes of the ends of the lines in FullContent.
    /// </summary>
    public List<int> LineEnds { get; }

    /// <summary>
    ///     One-indexed array of the character indexes of the starts of the lines in FullContent.
    /// </summary>
    public List<int> LineStarts { get; }

    /// <summary>
    ///     A dictionary mapping character index in FullContent to if a specific character is commented.  See IsCommented to
    ///     use.
    /// </summary>
    private ConcurrentDictionary<int, bool> CommentedStates { get; } = new();

    internal IEnumerable<(string, Boundary)> GetStringFromJsonPath(string Path)
    {
        lock (_jsonLock)
        {
            if (!_triedToConstructJsonDocument)
            {
                try
                {
                    _triedToConstructJsonDocument = true;
                    _jsonDocument = JsonDocument.Parse(FullContent);
                }
                catch (Exception e)
                {
                    _logger.LogError("Failed to parse {1} as a JSON document: {0}", e.Message, _filePath);
                    _jsonDocument = null;
                }
            }
        }

        if (_jsonDocument is null)
        {
            yield break;
        }

        var selector = JsonSelector.Parse(Path);

        var values = selector.Select(_jsonDocument.RootElement);

        var field = typeof(JsonElement).GetField("_idx", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field is null)
        {
            _logger.LogWarning("Failed to access _idx field of JsonElement.");
        }
        else
        {
            foreach (var ele in values)
                // Private access hack
                // The idx field is the start of the JSON element, including markup that isn't directly part of the element itself
                if (field.GetValue(ele) is int idx)
                {
                    // ele.ToString doesn't return the raw string from the json for booleans, it returns a capitalized False/True but JSON requires lower case false/true to parse
                    var eleString = ele.ValueKind is JsonValueKind.False ? "false" : ele.ValueKind is JsonValueKind.True ? "true" : ele.ToString();
                    if (eleString is { } denulledString)
                    {
                        var location = new Boundary
                        {
                            // Adjust the index to the start of the actual element
                            Index = FullContent[idx..].IndexOf(denulledString, StringComparison.Ordinal) + idx,
                            Length = eleString.Length
                        };
                        yield return (eleString, location);
                    }
                }
        }
    }

    /// <summary>
    ///     If this file is XML, attempts to return the the string contents of the specified XPath applied to the file.
    ///     If the path does not exist, or the file is not JSON, XML or YML returns null.
    ///     Method contains some heuristic behavior and may not cover all cases. 
    ///     Please report any issues with a sample XML and XPATH to reproduce.
    /// </summary>
    /// <param name="Path">XPath to query document with</param>
    /// <returns>Enumeration of string and Boundary tuples for the XPath matches. Boundary locations refer to the locations in the original document on disk.</returns>
    internal IEnumerable<(string, Boundary)> GetStringFromXPath(string Path, Dictionary<string, string> xpathNameSpaces)
    {
        lock (_xpathLock)
        {
            if (!_triedToConstructXPathDocument)
            {
                try
                {
                    _triedToConstructXPathDocument = true;
                    _xmlDoc = new XPathDocument(new StringReader(FullContent));
                }
                catch (Exception e)
                {
                    _logger.LogError("Failed to parse {1} as a XML document: {0}", e.Message, _filePath);
                    _xmlDoc = null;
                }
            }
        }

        if (_xmlDoc is null)
        {
            yield break;
        }

        var navigator = _xmlDoc.CreateNavigator();
        var query = navigator.Compile(Path);
        if (xpathNameSpaces.Any())
        {
            var manager = new XmlNamespaceManager(navigator.NameTable);
            foreach (var pair in xpathNameSpaces)
            {
                manager.AddNamespace(pair.Key, pair.Value);
            }
            query.SetContext(manager);
        }
        var nodeIter = navigator.Select(query);
        var minIndex = 0;
        while (nodeIter.MoveNext())
        {
            if (nodeIter.Current is null)
            {
                continue;
            }

            // We have to heuristically calculate the original indexes of the locations in the original document because the internal representation differs
            // For example it will convert <Tag Prop="Val"/> to <Tag Prop=\"Val\"/>

            // First we find the name, absolute position index
            var nameIndex = FullContent[minIndex..].IndexOf(nodeIter.Current.Name, StringComparison.Ordinal) + minIndex;
            // Then we calculate the absolute index of the end of the tag.
            // We can't use OuterXML property because the parser will inject the namespace if present into the OuterXML so it doesn't match the original text.
            var endTagIndex = FullContent[nameIndex..].IndexOf('>', StringComparison.Ordinal) + nameIndex;
            // If we are matching a tag itself, the previous char should be the open tag
            //  |
            //  v
            // <Tag>
            // If its a property it won't be
            //      |
            //      v
            // <Tag Prop="Value">
            var isProp = FullContent[(nameIndex - 1)] != '<';
            // Check for self-closing tag
            var selfClosedTag = FullContent[endTagIndex - 1] == '/';

            // This is for when we're capturing the value of a property of the tag rather than the tag itself
            if (isProp)
            {
                // Find the index of character after the next end tag index after the name
                var nextClosingIndexAfterName = endTagIndex+1;
                // If we have a self closing tag, we can use that index, otherwise we need the closure of this tag
                var offset = selfClosedTag ? endTagIndex : FullContent[nextClosingIndexAfterName..].IndexOf('>') + nextClosingIndexAfterName + 1;
                // Move the minimum index up to the end of the closing tag to avoid additioanl matches of the same values
                minIndex = selfClosedTag ? offset : FullContent[offset..].IndexOf('>') + offset + 1;
                var location = new Boundary
                {
                    // +2 for the \" before the value for the property
                    Index = nameIndex + nodeIter.Current.Name.Length + 2,
                    Length = nodeIter.Current.InnerXml.Length
                };
                yield return (nodeIter.Current.Value, location);
            }
            else
            {
                // Move the offset to the end of the opening tag
                var offset = selfClosedTag ? endTagIndex : FullContent[nameIndex..].IndexOf(nodeIter.Current.InnerXml, StringComparison.Ordinal) + nameIndex;
                // Move the minimum index up to the end of the closing tag
                minIndex = selfClosedTag ? offset : FullContent[offset..].IndexOf('>') + offset + 1;
                var location = new Boundary
                {
                    Index = offset,
                    Length = nodeIter.Current.InnerXml.Length
                };
                yield return (nodeIter.Current.Value, location);
            }
        }
    }

    /// <summary>
    /// Find the location of the provided prefix string if any in the text container between the provided start of line index and the current index
    /// </summary>
    /// <param name="startOfLineIndex">Minimum Character index in FullContent to locate Prefix</param>
    /// <param name="currentIndex">Maximal Chracter index in FullContent to locate Prefix</param>
    /// <param name="prefix">Prefix string to attempt to locate</param>
    /// <param name="multiline">If multi-line comments should be detected</param>
    /// <returns>The index of the specified prefix string in the FullContent if found, otherwise -1</returns>
    private int GetPrefixLocation(int startOfLineIndex, int currentIndex, string prefix, bool multiline)
    {
        // Find the first potential index of the prefix
        var prefixLoc = FullContent.LastIndexOf(prefix, currentIndex, StringComparison.Ordinal);
        if (prefixLoc != -1)
        {
            // TODO: Possibly support quoted multiline comment markers
            if (multiline)
            {
                return prefixLoc;
            }
            if (prefixLoc < startOfLineIndex)
            {
                return -1;
            }
            // Check how many quote marks occur on the line before the prefix location
            // TODO: This doesn't account for multi-line strings
            var numDoubleQuotes = FullContent[startOfLineIndex..prefixLoc].Count(x => x == '"');
            var numSingleQuotes = FullContent[startOfLineIndex..prefixLoc].Count(x => x == '\'');

            // If the number of quotes is odd, this is in a string, so not actually a comment prefix
            // It might be like var address = "http://contoso.com";
            if (numDoubleQuotes % 2 == 1 || numSingleQuotes % 2 == 1)
            {
                // The second argument is the maximal index to return since this calls LastIndexOf, subtract 1 to exclude this instance
                if ((prefixLoc -1) >= startOfLineIndex)
                {
                    return GetPrefixLocation(startOfLineIndex, prefixLoc - 1, prefix, multiline);
                }
                return -1;
            }
        }

        return prefixLoc;
    }

    /// <summary>
    ///     Populates the CommentedStates Dictionary based on the index and the provided comment prefix and suffix
    /// </summary>
    /// <param name="index">The character index in FullContent</param>
    /// <param name="prefix">The comment prefix</param>
    /// <param name="suffix">The comment suffix</param>
    private void PopulateCommentedStatesInternal(int index, string prefix, string suffix, bool multiline)
    {
        // Get the line boundary for the prefix location
        var startOfLine = GetLineBoundary(index);
        // Get the index of the prefix
        var prefixLoc = GetPrefixLocation(startOfLine.Index, index, prefix, multiline);

        if (prefixLoc != -1)
        {
            if (!CommentedStates.ContainsKey(prefixLoc))
            {
                var suffixLoc = FullContent.IndexOf(suffix, prefixLoc, StringComparison.Ordinal);
                if (suffixLoc == -1)
                {
                    suffixLoc = FullContent.Length - 1;
                }

                for (var i = prefixLoc; i <= suffixLoc; i++) CommentedStates[i] = true;
            }
        }
    }

    /// <summary>
    ///     Populate the CommentedStates Dictionary based on the provided index.
    /// </summary>
    /// <param name="index">The character index in FullContent to work based on.</param>
    public void PopulateCommentedState(int index)
    {
        var inIndex = index;
        if (index >= FullContent.Length)
        {
            index = FullContent.Length - 1;
        }

        if (index < 0)
        {
            index = 0;
        }

        // Populate true for the indexes of the most immediately preceding instance of the multiline comment type if found
        if (!string.IsNullOrEmpty(prefix) && !string.IsNullOrEmpty(suffix))
        {
            PopulateCommentedStatesInternal(index, prefix, suffix, true);
        }

        // Populate true for indexes of the most immediately preceding instance of the single-line comment type if found
        if (!CommentedStates.ContainsKey(index) && !string.IsNullOrEmpty(inline))
        {
            PopulateCommentedStatesInternal(index, inline, "\n", false);
        }

        var i = index;
        // Everything preceding this, including this, which doesn't have a commented state is
        // therefore not commented so we backfill
        while (!CommentedStates.ContainsKey(i) && i >= 0)
        {
            CommentedStates[i--] = false;
        }

        if (inIndex != index)
        {
            CommentedStates[inIndex] = CommentedStates[index];
        }
    }

    /// <summary>
    ///     Gets the text for a given boundary
    /// </summary>
    /// <param name="boundary">The boundary to get text for.</param>
    /// <returns></returns>
    public string GetBoundaryText(Boundary boundary)
    {
        if (boundary is null)
        {
            return string.Empty;
        }

        var start = Math.Max(boundary.Index, 0);
        var end = start + boundary.Length;
        start = Math.Min(FullContent.Length, start);
        end = Math.Min(FullContent.Length, end);
        return FullContent[start..end];
    }

    /// <summary>
    ///     Returns boundary for a given index in text
    /// </summary>
    /// <param name="index"> Position in text </param>
    /// <returns> Boundary </returns>
    public Boundary GetLineBoundary(int index)
    {
        Boundary result = new();

        for (var i = 0; i < LineEnds.Count; i++)
            if (LineEnds[i] >= index)
            {
                result.Index = LineStarts[i];
                result.Length = LineEnds[i] - LineStarts[i] + 1;
                break;
            }

        return result;
    }

    /// <summary>
    ///     Return content of the line
    /// </summary>
    /// <param name="line"> Line number (one-indexed) </param>
    /// <returns> Text </returns>
    public string GetLineContent(int line)
    {
        if (line >= LineEnds.Count)
        {
            line = LineEnds.Count - 1;
        }

        var index = LineEnds[line];
        var bound = GetLineBoundary(index);
        return FullContent.Substring(bound.Index, bound.Length);
    }

    /// <summary>
    ///     Returns location (Line, Column) for given index in text
    ///     If the index is beyond the end of the file, clamps to the end
    /// </summary>
    /// <param name="index"> Position in text (line is one-indexed)</param>
    /// <returns> Location </returns>
    public Location GetLocation(int index)
    {
        for (var i = 1; i < LineEnds.Count; i++)
        {
            if (LineEnds[i] >= index)
            {
                return new Location
                {
                    Column = index - LineStarts[i],
                    Line = i
                };
            }
        }

        // If the index is beyond the end of the file, clamp to the end of the file
        if (index > LineEnds[^1])
        {
            return new Location()
            {
                Column = LineEnds[^1] - LineStarts[^1],
                Line = LineEnds.Count
            };
        }

        return new Location();
    }

    public bool IsCommented(int index)
    {
        if (!CommentedStates.ContainsKey(index))
        {
            PopulateCommentedState(index);
        }

        return CommentedStates[index];
    }

    /// <summary>
    ///     Check whether the boundary in a text matches the scope of a search pattern (code, comment etc.)
    /// </summary>
    /// <param name="scopes"> The scopes to check </param>
    /// <param name="boundary"> Boundary in the text </param>
    /// <returns> True if boundary is in a provided scope </returns>
    public bool ScopeMatch(IList<PatternScope> scopes, Boundary boundary)
    {
        if (scopes is null || !scopes.Any() || scopes.Contains(PatternScope.All))
        {
            return true;
        }

        if (Languages.IsAlwaysCommented(Language))
        {
            return scopes.Contains(PatternScope.Comment);
        }
        
        if (scopes.Contains(PatternScope.All) || string.IsNullOrEmpty(prefix))
        {
            return true;
        }

        var isInComment = IsCommented(boundary.Index);

        return (!isInComment && scopes.Contains(PatternScope.Code)) ||
               (isInComment && scopes.Contains(PatternScope.Comment));
    }

    internal IEnumerable<(string, Boundary)> GetStringFromYmlPath(string Path)
    {
        lock (_yamlLock)
        {
            if (!_triedToConstructYmlDocument)
            {
                try
                {
                    _triedToConstructYmlDocument = true;
                    _ymlDocument = new YamlStream();
                    _ymlDocument.Load(new StringReader(FullContent));
                }
                catch (Exception e)
                {
                    _logger.LogError("Failed to parse {1} as a YML document: {0}", e.Message, _filePath);
                    _ymlDocument = null;
                }
            }
        }

        if (!(_ymlDocument?.Documents.Count > 0))
        {
            yield break;
        }

        var theDocuments = _ymlDocument.Documents.ToImmutableArray();
        foreach (var match in theDocuments.Select(document => document.RootNode.Query(Path)).SelectMany(matches => matches))
        {
            // TODO: Should update Boundary object to have longs intead of int rather than this casting
            yield return (match.ToString(),
                new Boundary() { Index = (int)match.Start.Index, Length = (int)match.End.Index - (int)match.Start.Index });
        }
    }
}