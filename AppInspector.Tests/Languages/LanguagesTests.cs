﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using Microsoft.ApplicationInspector.Logging;
using Microsoft.ApplicationInspector.RulesEngine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Serilog.Events;

namespace AppInspector.Tests.Languages;

[TestClass]
[ExcludeFromCodeCoverage]
public class LanguagesTests
{
    private readonly string comments_z = @"
[
  {
    ""language"": [
      ""z""
    ],
    ""inline"": ""//"",
    ""prefix"": ""/*"",
    ""suffix"": ""*/""
  }
]";

    private readonly string languages_z = @"
[
  {
    ""name"": ""z"",
    ""extensions"": [ "".z"", "".xw"" ],
    ""type"": ""code""
  }
]";

    private ILoggerFactory _factory = new NullLoggerFactory();
    private string invalidTestCommentsPath = string.Empty;
    private string invalidTestLanguagesPath = string.Empty;
    private string testCommentsPath = string.Empty;

    private string testLanguagesPath = string.Empty;

    [TestInitialize]
    public void InitOutput()
    {
        Directory.CreateDirectory(TestHelpers.GetPath(TestHelpers.AppPath.testOutput));
        testLanguagesPath = Path.Combine(TestHelpers.GetPath(TestHelpers.AppPath.testOutput), "test_languages.json");
        testCommentsPath = Path.Combine(TestHelpers.GetPath(TestHelpers.AppPath.testOutput), "test_comments.json");
        File.WriteAllText(testLanguagesPath, languages_z);
        File.WriteAllText(testCommentsPath, comments_z);
        invalidTestLanguagesPath = Path.Combine(TestHelpers.GetPath(TestHelpers.AppPath.testOutput),
            "test_languages_invalid.json");
        invalidTestCommentsPath =
            Path.Combine(TestHelpers.GetPath(TestHelpers.AppPath.testOutput), "test_comments_invalid.json");
        File.WriteAllText(invalidTestLanguagesPath,
            languages_z.Trim().Substring(1)); // Not a valid json array, should be missing the opening [
        File.WriteAllText(invalidTestCommentsPath,
            comments_z.Trim().Substring(1)); // Not a valid json, should be missing the opening [
        _factory = new LogOptions { ConsoleVerbosityLevel = LogEventLevel.Verbose }.GetLoggerFactory();
    }

    [ClassCleanup]
    public static void CleanUp()
    {
        try
        {
            Directory.Delete(TestHelpers.GetPath(TestHelpers.AppPath.testOutput), true);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
        }
    }

    [TestMethod]
    public void DetectCustomLanguage()
    {
        var languages =
            Microsoft.ApplicationInspector.RulesEngine.Languages.FromConfigurationFiles(_factory, testCommentsPath,
                testLanguagesPath);
        Assert.IsTrue(languages.FromFileNameOut("afilename.z", out var language));
        Assert.AreEqual("z", language.Name);
        Assert.IsFalse(languages.FromFileNameOut("afilename.c", out var _));
    }

    [TestMethod]
    public void EmptyLanguagesOnInvalidCommentsAndLanguages()
    {
        Assert.ThrowsException<JsonException>(() =>
            Microsoft.ApplicationInspector.RulesEngine.Languages.FromConfigurationFiles(_factory,
                invalidTestCommentsPath));
        Assert.ThrowsException<JsonException>(() =>
            Microsoft.ApplicationInspector.RulesEngine.Languages.FromConfigurationFiles(_factory, null,
                invalidTestLanguagesPath));
    }

    [TestMethod]
    public void DetectLanguageAsFileNameLanguage()
    {
        Microsoft.ApplicationInspector.RulesEngine.Languages languages = new(_factory);
        Assert.IsTrue(languages.FromFileNameOut("package.json", out var language));
        Assert.AreEqual("package.json", language.Name);
    }

    [DataRow(null, false)] // No way to determine language
    [DataRow("", false)] // No way to determine language
    [DataRow("validfilename.json", false)] //This test uses the .z test comments and languages from this file.
    [DataRow("validfilename.z", true)]
    [TestMethod]
    public void ReturnFalseWithInvalidFilename(string? filename, bool expected)
    {
        var languages =
            Microsoft.ApplicationInspector.RulesEngine.Languages.FromConfigurationFiles(_factory, testCommentsPath,
                testLanguagesPath);
        Assert.AreEqual(expected, languages.FromFileNameOut(filename!, out _));
    }
    
        
    [TestMethod]
    public void ScopeMatchAlwaysCommented()
    {
        var _languages = new Microsoft.ApplicationInspector.RulesEngine.Languages();
        var textContainer = new TextContainer("Hello this is some content", "plaintext", _languages);
        var boundary = new Boundary() { Index = 1, Length = 2 };
        Assert.IsTrue(textContainer.ScopeMatch(new List<PatternScope>() { PatternScope.All }, boundary));
        Assert.IsFalse(textContainer.ScopeMatch(new List<PatternScope>() { PatternScope.Code }, boundary));
        Assert.IsTrue(textContainer.ScopeMatch(new List<PatternScope>() { PatternScope.Comment }, boundary));
    }
}