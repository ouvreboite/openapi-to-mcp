﻿using System.Text.RegularExpressions;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Interfaces;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Microsoft.OpenApi.Validations;
using Microsoft.OpenApi.Writers;

namespace OpenApiToMcp.OpenApi;

public class OpenApiParser
{
    public async Task<(OpenApiDocument,OpenApiDiagnostic)> Parse(string openapiFileOrUrl, string? hostOverride, string? bearerToken, ToolNamingStrategy toolNamingStrategy)
    {
        //if starts with http, treat as url
        string? openApiDocumentAsString;
        string? host = null;
        if (openapiFileOrUrl.StartsWith("http"))
        {
            host = new Uri(openapiFileOrUrl, UriKind.Absolute).GetLeftPart(UriPartial.Authority);
            var httpClient = new HttpClient()
                .WithOpenApiToMcpUserAgent()
                .WithBearerToken(bearerToken);
            var stream = await httpClient.GetStreamAsync(openapiFileOrUrl);
            openApiDocumentAsString = await new StreamReader(stream).ReadToEndAsync();
        }
        else
        {
            if (!File.Exists(openapiFileOrUrl))
                throw new ArgumentException($"Openapi file does not exist: {openapiFileOrUrl}");
            openApiDocumentAsString =  await File.ReadAllTextAsync(openapiFileOrUrl);
        }
        
        //read and convert local refs
        var openApiDocumentWithRef = new OpenApiStringReader(new OpenApiReaderSettings { RuleSet = RuleSet(toolNamingStrategy)})
            .Read(openApiDocumentAsString, out var diagnostic);
        var openApiDocumentWithoutRefAsString = new StringWriter();
        openApiDocumentWithRef.SerializeAsV3(new OpenApiJsonWriter(
            openApiDocumentWithoutRefAsString,
            new OpenApiWriterSettings() { InlineLocalReferences = true }
        ));
        var openApiDocument = new OpenApiStringReader().Read(openApiDocumentWithoutRefAsString.ToString(), out _);
        
        //until https://github.com/microsoft/OpenAPI.NET/pull/2278 is released
        openApiDocument.Components ??= new OpenApiComponents();
        openApiDocument.Components.SecuritySchemes = openApiDocumentWithRef.Components?.SecuritySchemes;
        
        //handle relative servers url and inject host override
        foreach (var server in openApiDocument.Servers)
        {
            if (Uri.TryCreate(server.Url, UriKind.Relative, out var _))
                server.Url = (hostOverride ?? host) + server.Url;
            if (Uri.TryCreate(server.Url, UriKind.Absolute, out var abs) && hostOverride != null)
                server.Url = hostOverride + abs.PathAndQuery+abs.Fragment;
        }
        if(!openApiDocument.Servers.Any())
            openApiDocument.Servers.Add(new OpenApiServer{Url = hostOverride ?? host});
        
        return (openApiDocument,diagnostic);
    }

    private ValidationRuleSet RuleSet(ToolNamingStrategy toolNamingStrategy)
    {
        var rules = ValidationRuleSet.GetDefaultRuleSet().Rules;
        rules.Add(OpenApiUtils.OperationMustTranslateToValidToolName(toolNamingStrategy));
        rules.Add(OpenApiUtils.ExtensionHasValidType<OpenApiInfo,OpenApiString>(OpenApiUtils.XMcpInstructions));
        rules.Add(OpenApiUtils.ExtensionHasValidType<OpenApiOperation,OpenApiString>(OpenApiUtils.XMcpToolName));
        rules.Add(OpenApiUtils.ExtensionHasValidType<OpenApiOperation,OpenApiString>(OpenApiUtils.XMcpToolDescription));
        rules.Add(OpenApiUtils.ExtensionHasValidType<OpenApiOperation,OpenApiBoolean>(OpenApiUtils.XMcpToolEnabled));
        return new ValidationRuleSet(rules);
    }
}

public static class OpenApiUtils{
    private const int ToolNameMaxLength = 64;
    public static readonly Regex ValidToolName = new("^[a-zA-Z0-9_-]{1,"+ToolNameMaxLength+"}$");
    public const string XMcpInstructions = "x-mcp-instructions";
    public const string XMcpToolName = "x-mcp-tool-name";
    public const string XMcpToolDescription = "x-mcp-tool-description";
    public const string XMcpToolEnabled = "x-mcp-tool-enabled";

    public static ValidationRule<OpenApiPaths> OperationMustTranslateToValidToolName(ToolNamingStrategy toolNamingStrategy) =>
        new(nameof(OperationMustTranslateToValidToolName),
            (context, item) =>
            {
                foreach (var pathName in item.Keys)
                {
                    context.Enter(pathName);
                    context.Enter("operations");
                    foreach (var operationType in item[pathName].Operations.Keys)
                    {
                        context.Enter(operationType.ToString());
                        
                        var operation = item[pathName].Operations[operationType];
                        var toolName = operation.McpToolName(pathName, operationType, toolNamingStrategy);
                        if(toolName.Length > ToolNameMaxLength)
                            context.CreateError(nameof(OperationMustTranslateToValidToolName),$"Operation {operationType} {pathName} translate to a too long tool name: {toolName}");
                        else if(!ValidToolName.IsMatch(toolName))
                            context.CreateError(nameof(OperationMustTranslateToValidToolName),$"Operation {operationType} {pathName} translate to an invalid tool name: {toolName}");
                        
                        context.Exit();
                    }
                    context.Exit();
                    context.Exit();
                }
            }
        );

    public static ValidationRule<T> ExtensionHasValidType<T, TE>(string extension) where T : IOpenApiExtensible
    {
        var name = $"{extension}HasType{typeof(TE).Name}";
        return new ValidationRule<T>(name, (context, item) =>
        {
            if (!item.Extensions.TryGetValue(extension, out var ext))
                return;
            if (ext is not TE)
            {
                context.CreateError(name,$"Extension {extension} must have a {typeof(TE).Name} value");
            }
        });
    }

    public static string McpToolName(this OpenApiOperation operation, string path, OperationType type, ToolNamingStrategy toolNamingStrategy)
    {
        operation.Extensions.TryGetValue(XMcpToolName, out var extension);
        var toolNameExtension = (extension as OpenApiString)?.Value;
        var toolNameOperationId = operation.OperationId;
        var toolNameVerbAndPath = type + path.Replace("{", "").Replace("}", "").Replace("/", "_");

        return toolNamingStrategy switch
        {
            ToolNamingStrategy.extension => toolNameExtension ?? "",
            ToolNamingStrategy.operationid => toolNameOperationId ?? "",
            ToolNamingStrategy.verbandpath => toolNameVerbAndPath,
            ToolNamingStrategy.extension_or_operationid_or_verbandpath => toolNameExtension ?? toolNameOperationId ?? toolNameVerbAndPath,
            _ => throw new ArgumentOutOfRangeException(nameof(toolNamingStrategy), toolNamingStrategy, null)
        };
    }
    
    public static bool McpToolEnabled(this OpenApiOperation operation)
    {
        operation.Extensions.TryGetValue(XMcpToolEnabled, out var extension);
        var enabled = extension as OpenApiBoolean;
        return enabled?.Value ?? true;
    }
    
    public static string? McpToolDescription(this OpenApiOperation operation, OpenApiPathItem pathItem)
    {
        operation.Extensions.TryGetValue(XMcpToolDescription, out var extension);
        var description = extension as OpenApiString;
        return  description?.Value ??
                operation.Description ?? 
                pathItem.Description;
    }

    
    public static string? McpInstructions(this OpenApiInfo info)
    {
        info.Extensions.TryGetValue(XMcpInstructions, out var extension);
        var instructions = extension as OpenApiString;
        return instructions?.Value;
    }
}

