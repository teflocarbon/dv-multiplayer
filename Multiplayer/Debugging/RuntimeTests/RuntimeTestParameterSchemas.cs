#if DEBUG
using System;
using System.Collections.Generic;

namespace Multiplayer.Debugging.RuntimeTests;

/// <summary>
/// Operator-facing JSON Schemas for the dashboard command console. Runtime parameters remain
/// strings on the wire; format, enum, default, and description metadata make that contract usable
/// without reading the command implementation.
/// </summary>
internal static class RuntimeTestParameterSchemas
{
    private static readonly HashSet<string> parameterless = new(StringComparer.Ordinal)
    {
        "runtime.self-check", "runtime.control-status", "runtime.neutralize",
        "environment.status", "environment.save-catalog", "environment.host-save",
        "environment.host-latest-save", "inventory.prefab-catalog", "train.track-catalog",
        "train.fixture-location-catalog", "train.fixture-catalog",
        "train.item-lab-index-status", "train.item-lab-index-add-look",
        "train.item-lab-index-add-nearby", "train.item-lab-index-clear"
    };

    public static Dictionary<string, object> For(string command)
    {
        if (parameterless.Contains(command)) return Schema();
        return command switch
        {
            "runtime.wait" => Schema(new[] { "seconds" },
                P("seconds", "number", "Real-time dwell interval in seconds.", "1")),
            "runtime.interaction-mode" => Schema(new[] { "mode" },
                P("mode", "string", "Player interaction mode.", "pickup", "pickup", "screenspace")),
            "environment.connect-client" => Schema(null,
                P("address", "string", "Server address.", "127.0.0.1"),
                P("port", "integer", "Server port.")),
            "player.teleport" => Schema(new[] { "x", "y", "z" }, Position()),
            "player.position" => Schema(),
            "item.look-at" or "item.pickup" => Schema(new[] { "netId" },
                P("netId", "integer", "Networked item identifier.")),
            "item.drop" => Schema(),
            "item.throw" => Schema(null,
                P("speed", "number", "Throw speed in metres per second.", "8")),
            "inventory.inspect" => Schema(null,
                P("shellNetId", "integer", "Optional container fixture NetId."),
                P("itemNetId", "integer", "Optional item fixture NetId."),
                P("materializedNetId", "integer", "Optional materialized item NetId.")),
            "inventory.fixture-create" => Schema(new[] { "prefabName" },
                P("prefabName", "string", "DV inventory prefab name."),
                P("placement", "string", "Initial placement.", "inventory", "inventory", "hand", "world"),
                P("ownerPlayerId", "integer", "Persistent owner player id."),
                P("holderPlayerId", "integer", "Current holder player id."),
                P("slot", "integer", "Requested inventory slot.", "-1"),
                P("x", "number", "Absolute world X for world placement."),
                P("y", "number", "Absolute world Y for world placement."),
                P("z", "number", "Absolute world Z for world placement."),
                P("testTag", "string", "Bulk-cleanup tag. Scenarios populate this automatically.")),
            "inventory.fixture-place" => Schema(new[] { "netId", "fixtureToken" },
                P("netId", "integer", "Fixture NetId."), P("fixtureToken", "string", "Fixture identity token."),
                P("placement", "string", "Destination placement.", "inventory", "inventory", "hand", "world"),
                P("holderPlayerId", "integer", "Destination holder player id."), P("slot", "integer", "Requested slot.", "-1"),
                P("x", "number", "Absolute world X."), P("y", "number", "Absolute world Y."), P("z", "number", "Absolute world Z.")),
            "inventory.fixture-destroy" => Schema(new[] { "netId", "fixtureToken" },
                P("netId", "integer", "Fixture NetId."), P("fixtureToken", "string", "Fixture identity token.")),
            "inventory.fixture-status" => Schema(null,
                P("testTag", "string", "Only return fixtures with this test tag.")),
            "inventory.fixture-destroy-tag" => Schema(new[] { "testTag" },
                P("testTag", "string", "Destroy every host-authoritative fixture carrying this exact tag.")),
            "inventory.fixture-clean-local" => Schema(null,
                P("itemFixtureToken", "string", "Item fixture token."), P("shellFixtureToken", "string", "Container fixture token."),
                P("itemNetId", "integer", "Item NetId."), P("shellNetId", "integer", "Container NetId.")),
            "inventory.local-place" => Schema(new[] { "netId" },
                P("netId", "integer", "Networked item identifier."),
                P("placement", "string", "Destination placement.", "inventory", "inventory", "hand", "world"),
                P("slot", "integer", "Requested slot.", "-1"),
                P("x", "number", "Absolute world X."), P("y", "number", "Absolute world Y."), P("z", "number", "Absolute world Z.")),
            "train.track-nearest" => Schema(null,
                P("radius", "number", "Search radius in metres.", "250")),
            "train.fixture-teleport-location" => Schema(new[] { "locationId" },
                P("locationId", "string", "Named fixture location id.")),
            "train.fixture-create" => Schema(new[] { "fixtureId", "locationId", "liveries" },
                P("fixtureId", "string", "Unique fixture/test tag."), P("locationId", "string", "Named track location id."),
                P("liveries", "string", "Comma-separated livery ids."), P("roles", "string", "Comma-separated car roles."),
                P("withTrackDirection", "boolean", "Orient with track direction.", "true"),
                P("preventDerailment", "boolean", "Enable derailment protection.", "true"),
                P("protectFuses", "boolean", "Protect locomotive fuses.", "true"),
                P("sustainEngine", "boolean", "Keep engine systems running.", "true"),
                P("preventDamage", "boolean", "Suppress fixture damage.", "true"),
                P("maximumSpeedKph", "number", "Automatic controller speed ceiling.", "80")),
            "train.fixture-observe" => Schema(new[] { "carNetId" }, P("carNetId", "integer", "Fixture car NetId.")),
            "train.fixture-status" => Schema(null, P("fixtureId", "string", "Optional exact fixture id.")),
            "train.fixture-acquire-route" => FixtureIdSchema(),
            "train.fixture-start" => Schema(new[] { "fixtureId" }, P("fixtureId", "string", "Fixture id."),
                P("targetSpeedKph", "number", "Target speed.", "20"), P("reverser", "number", "Reverser direction (-1 to 1).", "1")),
            "train.fixture-stop" => FixtureIdSchema(),
            "train.fixture-wait-motion" => Schema(new[] { "fixtureId" }, P("fixtureId", "string", "Fixture id."),
                P("minimumSpeedKph", "number", "Required minimum speed.", "1")),
            "train.fixture-set-control" => Schema(new[] { "fixtureId", "control", "value" },
                P("fixtureId", "string", "Fixture id."), P("control", "string", "Locomotive control name."), P("value", "number", "Control value.")),
            "train.fixture-relocate" => Schema(new[] { "fixtureId", "locationId" },
                P("fixtureId", "string", "Fixture id."), P("locationId", "string", "Destination location id."),
                P("withTrackDirection", "boolean", "Orient with track direction.", "true")),
            "train.fixture-place-player" => Schema(new[] { "carNetId" }, P("carNetId", "integer", "Fixture car NetId."),
                P("anchor", "string", "Placement anchor.", "interior-center", "cab", "interior-center", "front-platform", "rear-platform")),
            "train.fixture-evacuate-player" => Schema(new[] { "carNetId" }, P("carNetId", "integer", "Fixture car NetId."),
                P("distance", "number", "Evacuation distance in metres.", "12")),
            "train.fixture-cleanup" => Schema(new[] { "fixtureId" }, P("fixtureId", "string", "Fixture id."),
                P("ignoreMissing", "boolean", "Treat an absent fixture as clean.", "false")),
            "train.fixture-cleanup-orphans" => Schema(null, P("fixtureId", "string", "Optional exact orphan fixture id.")),
            "train.item-observe-motion" or "train.item-lab-status" or "train.item-lab-verify" => Schema(new[] { "carNetId" },
                P("carNetId", "integer", "Train car NetId.")),
            "train.item-lab-index-remove" => Schema(new[] { "netId" }, P("netId", "integer", "Indexed item NetId.")),
            "train.item-lab-throw-from-view" => Schema(null,
                P("sourceNetId", "integer", "Indexed projectile item NetId."), P("speed", "number", "Launch speed.", "8")),
            "train.item-lab-arrange" => Schema(new[] { "carNetId", "itemsJson" },
                P("carNetId", "integer", "Train car NetId."), P("itemsJson", "string", "JSON array of item poses.")),
            "train.item-lab-wake" => Schema(new[] { "netId" }, P("netId", "integer", "Item NetId."),
                P("reason", "string", "Wake reason.", "debug")),
            "train.item-lab-throw-at" => Schema(new[] { "sourceNetId", "targetNetId" },
                P("sourceNetId", "integer", "Projectile item NetId."), P("targetNetId", "integer", "Target item NetId."),
                P("speed", "number", "Launch speed.", "8")),
            "train.item-lab-wait-cycle" => Schema(new[] { "netId" }, P("netId", "integer", "Observed item NetId."),
                P("timeoutSeconds", "number", "Maximum wait.", "45")),
            _ => OpenSchema()
        };
    }

    private static Dictionary<string, object> FixtureIdSchema() =>
        Schema(new[] { "fixtureId" }, P("fixtureId", "string", "Fixture id."));

    private static KeyValuePair<string, object>[] Position() => new[]
    {
        P("x", "number", "Absolute world X."), P("y", "number", "Absolute world Y."),
        P("z", "number", "Absolute world Z.")
    };

    private static KeyValuePair<string, object> P(string name, string type, string description,
        string defaultValue = null, params string[] values)
    {
        Dictionary<string, object> value = new(StringComparer.Ordinal)
        {
            ["type"] = "string", ["format"] = type, ["description"] = description
        };
        if (defaultValue != null) value["default"] = defaultValue;
        if (values?.Length > 0) value["enum"] = values;
        return new KeyValuePair<string, object>(name, value);
    }

    private static Dictionary<string, object> Schema(string[] required = null,
        params KeyValuePair<string, object>[] propertyGroups)
    {
        Dictionary<string, object> properties = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object> property in propertyGroups)
            properties[property.Key] = property.Value;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["type"] = "object", ["properties"] = properties,
            ["required"] = required ?? Array.Empty<string>(), ["additionalProperties"] = true
        };
    }

    private static Dictionary<string, object> OpenSchema() => new(StringComparer.Ordinal)
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        ["type"] = "object",
        ["description"] = "This command accepts string-valued debug parameters; no stricter schema is currently declared.",
        ["additionalProperties"] = new Dictionary<string, object> { ["type"] = "string" }
    };
}
#endif
