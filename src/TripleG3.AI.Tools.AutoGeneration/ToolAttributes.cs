namespace TripleG3.AI.Tools.AutoGeneration;

[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public class ToolAttribute(string name, string description, string friendlyUIDescription, bool isManuallyRegistered = false) : Attribute
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public string FriendlyUIDescription { get; } = friendlyUIDescription;
    public bool IsManuallyRegistered { get; } = isManuallyRegistered;
    public bool IsAlwaysAvailable { get; set; }
    public bool IsLongRunning { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public string[]? Aliases { get; set; }
    public string[]? Categories { get; set; }
    public string[]? Replaces { get; set; }
    public bool IsDeprecated { get; set; }
    public string? DeprecationMessage { get; set; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class ToolCategoryAttribute(string name, string description) : Attribute
{
    public string Name { get; } = name;
    public string Description { get; } = description;
}

[AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class ToolRequiredAttribute : Attribute;

[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Method, Inherited = true, AllowMultiple = true)]
public class ToolParameterAttribute(string name, string description, bool isRequired = true) : Attribute
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public virtual Type? Type => null;
    public bool IsRequired { get; } = isRequired;
}

[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Method, Inherited = true, AllowMultiple = true)]
public sealed class ToolParameterAttribute<T>(string name, string description, bool isRequired = true) : ToolParameterAttribute(name, description, isRequired)
{
    public override Type? Type => typeof(T);
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, Inherited = true, AllowMultiple = false)]
public sealed class ToolSchemaAttribute(string description) : Attribute
{
    public string Description { get; } = description;
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Field, Inherited = true, AllowMultiple = false)]
public sealed class ToolResultAttribute(string description) : Attribute
{
    public string Description { get; } = description;
}

[AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
public sealed class ToolEnumDescriptionAttribute(string description) : Attribute
{
    public string Description { get; } = description;
}