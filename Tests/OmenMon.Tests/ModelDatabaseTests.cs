using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace OmenMon.Tests;

public class ModelDatabaseTests {
    private static readonly string[] RequiredRegisterElements = {
        "FanLevelReg0",
        "FanLevelReg1",
        "FanRateReadReg0",
        "FanRateReadReg1",
        "FanRateWriteReg0",
        "FanRateWriteReg1",
        "FanSpeedReg0",
        "FanSpeedReg1",
        "CountdownReg",
        "ManualReg",
        "ModeReg",
        "SwitchReg"
    };

    private static readonly string[] OptionalByteElements = {
        "ManualValueOn",
        "ManualValueOff",
        "TempCpuReg",
        "TempGpuReg"
    };

    private static readonly Lazy<XDocument> Config = new(() =>
        XDocument.Load(GetConfigPath(), LoadOptions.SetLineInfo));

    private static string GetConfigPath() {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while(current is not null) {
            var candidate = Path.Combine(current.FullName, "OmenMon.xml");
            if(File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException("Unable to locate OmenMon.xml by walking parent directories from test output.");
    }

    private static IReadOnlyList<XElement> GetModels() {
        return Config.Value
            .Descendants("Models")
            .Elements("Model")
            .ToList();
    }

    private static string ModelTag(XElement model) {
        var lineInfo = (IXmlLineInfo) model;
        var line = lineInfo.HasLineInfo() ? lineInfo.LineNumber.ToString(CultureInfo.InvariantCulture) : "?";
        return $"ProductId='{(string?) model.Attribute("ProductId") ?? ""}', DisplayName='{(string?) model.Attribute("DisplayName") ?? ""}', Line={line}";
    }

    public static IEnumerable<object[]> KnownModels() {
        foreach(var model in GetModels()) {
            yield return new object[] {
                (string?) model.Attribute("ProductId") ?? string.Empty,
                (string?) model.Attribute("DisplayName") ?? string.Empty,
                model
            };
        }
    }

    [Fact]
    public void KnownModels_SectionExistsAndContainsEntries() {
        var models = GetModels();
        Assert.True(models.Count > 0,
            "No <Model> entries found under <Models> in OmenMon.xml.");
    }

    [Theory]
    [MemberData(nameof(KnownModels))]
    public void KnownModel_HasRequiredFields_WithDetailedErrors(string productId, string displayName, XElement model) {
        var errors = new List<string>();
        var tag = ModelTag(model);

        if(string.IsNullOrWhiteSpace(productId))
            errors.Add($"[{tag}] Missing or empty ProductId attribute.");
        if(string.IsNullOrWhiteSpace(displayName))
            errors.Add($"[{tag}] Missing or empty DisplayName attribute.");

        foreach(var elementName in RequiredRegisterElements) {
            var node = model.Element(elementName);
            if(node is null) {
                errors.Add($"[{tag}] Missing required <{elementName}> element.");
                continue;
            }

            if(!byte.TryParse(node.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                errors.Add($"[{tag}] <{elementName}> has invalid byte value '{node.Value}'.");
        }

        foreach(var elementName in OptionalByteElements) {
            var node = model.Element(elementName);
            if(node is not null && !byte.TryParse(node.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                errors.Add($"[{tag}] Optional <{elementName}> has invalid byte value '{node.Value}'.");
        }

        var manualOnPresent = model.Element("ManualValueOn") is not null;
        var manualOffPresent = model.Element("ManualValueOff") is not null;
        if(manualOnPresent != manualOffPresent)
            errors.Add($"[{tag}] Manual override is incomplete: ManualValueOn and ManualValueOff must be specified together.");

        Assert.True(errors.Count == 0,
            $"Model validation failed for ProductId='{productId}', DisplayName='{displayName}'.{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
    }

    [Fact]
    public void KnownModel_ProductIdsAreUnique() {
        var duplicates = GetModels()
            .Select(model => (string?) model.Attribute("ProductId") ?? string.Empty)
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .Select(group => $"Duplicate ProductId '{group.Key}' appears {group.Count()} times")
            .ToList();

        Assert.True(duplicates.Count == 0,
            "Model ProductId duplicates found:" + Environment.NewLine + string.Join(Environment.NewLine, duplicates));
    }

    [Fact]
    public void KnownModels_RequiredFieldShapeIsConsistent() {
        var errors = new List<string>();

        foreach(var model in GetModels()) {
            foreach(var elementName in RequiredRegisterElements) {
                if(model.Elements(elementName).Count() != 1)
                    errors.Add($"[{ModelTag(model)}] Expected exactly one <{elementName}> element.");
            }
        }

        Assert.True(errors.Count == 0,
            "Model field-shape errors found:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }
}
