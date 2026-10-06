using AnalyzerWebApi.Services;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Identity;
using OpenAI.Files;
using OpenAI.Responses;
using OpenAI.VectorStores;

public class FoundryServices : IFoundryServices
{
    private readonly string _endpoint;
    private readonly string _baseAgentName;
    private readonly AIProjectClient _projectClient;
    private readonly ProjectResponsesClient _baseResponseClient;
    private readonly ILogger<FoundryServices> _logger;

    public FoundryServices(string endpoint, string baseAgentName, ILogger<FoundryServices> logger)
    {
        _endpoint = endpoint;
        _baseAgentName = baseAgentName;
        _logger = logger;

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Initializing FoundryServices with Endpoint: {Endpoint}, BaseAgentName: {BaseAgentName}", _endpoint, _baseAgentName);
        }

        _projectClient = new(new Uri(_endpoint), new DefaultAzureCredential());

        _baseResponseClient = _projectClient.ProjectOpenAIClient.GetProjectResponsesClientForAgent(_baseAgentName);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("BaseResponseClient initialized for agent {BaseAgentName}", _baseAgentName);
        }
    }

    public async Task<string> AnalyzeDocumentWithFileSearchAsync(BinaryData documentBytes, string fileName, string documentMimeType, string expectedJsonSchema, string userInstructions)
    {
        _logger.LogInformation("Starting AnalyzeDocumentWithFileSearchAsync");
        
        var uniqueId = Guid.NewGuid().ToString().Substring(0, 8);
        Console.WriteLine($"Starting new agent from {_baseAgentName}...  File to be uploaded: {fileName}, run identifier: {uniqueId}");

        // Upload a file to be used in the VectorStore tool
        var uploadedFile = await _projectClient.ProjectOpenAIClient.GetProjectFilesClient().UploadFileAsync(
            documentBytes.ToStream(),
            filename: $"{Path.GetFileNameWithoutExtension(fileName)}-{uniqueId}{Path.GetExtension(fileName)}",
            FileUploadPurpose.Assistants
        );

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Uploaded file with ID: {FileId}", uploadedFile.Value.Id);
        }

        // Create the VectorStore and provide it with uploaded file ID.
        VectorStoreCreationOptions vectorStoreOptions = new()
        {
            Name = $"index_{_baseAgentName}_{uniqueId}",
            FileIds = { uploadedFile.Value.Id },
        };
        var vectorStore = await _projectClient.ProjectOpenAIClient.GetProjectVectorStoresClient().CreateVectorStoreAsync(options: vectorStoreOptions);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Created VectorStore with ID: {VectorStoreId}", vectorStore.Value.Id);
        }
        
        // Create the agent with the file-search tool that uses the VectorStore created above.
        var baseAgentRecord = await _projectClient.AgentAdministrationClient.GetAgentAsync(_baseAgentName);

        var baseAgentDefinition = baseAgentRecord.Value.GetLatestVersion().Definition as DeclarativeAgentDefinition
            ?? throw new InvalidOperationException(
                $"L’agent « {_baseAgentName} » doit être un agent de type prompt.");

        var newAgentDefinition = ProjectsAgentDefinition.CreatePromptAgentDefinition(baseAgentDefinition.Model);
        newAgentDefinition.Instructions = baseAgentDefinition.Instructions;
        newAgentDefinition.Temperature = baseAgentDefinition.Temperature;
        newAgentDefinition.Tools.Add(
            ResponseTool.CreateFileSearchTool(
                vectorStoreIds: [vectorStore.Value.Id]
            )
        );

        var newAgentVersion = await _projectClient.AgentAdministrationClient.CreateAgentVersionAsync(
            agentName: $"{_baseAgentName}-{uniqueId}",
            options: new(newAgentDefinition)
        );
        //var newAgentRecord = await _projectClient.AgentAdministrationClient.GetAgentAsync(newAgentVersion.Value.Name);
        Console.WriteLine($"Created agent with name: {newAgentVersion.Value.Name} and version: {newAgentVersion.Value.Version}");

        ProjectResponsesClient responsesClient = _projectClient.ProjectOpenAIClient.GetProjectResponsesClientForAgent(newAgentVersion.Value.Name);
        
        var prompt = @$"
                        Analyse le document « {fileName} », de type {documentMimeType}.
                        Utilise l’outil file_search pour rechercher les informations nécessaires.
                        Appuie ta réponse sur le contenu du document.
                        Si les informations sont insuffisantes, indique-le clairement.

                        Question ou demande de l’utilisateur :
                        {userInstructions}

                        Réponds exclusivement en français.
                        Respecte le schéma JSON suivant, en conservant ses clés techniques
                        et en rédigeant les textes destinés à l’utilisateur en français :
                        {expectedJsonSchema}";

        CreateResponseOptions options = new()
        {
            InputItems =
            {
                ResponseItem.CreateUserMessageItem(
                [
                    ResponseContentPart.CreateInputTextPart(prompt),
                ])
            },
        };

        // Chat with the agent to answer questions
        ResponseResult response = await responsesClient.CreateResponseAsync(options);

        // Cleanup - delete the agent and the vector store
        await _projectClient.AgentAdministrationClient.DeleteAgentAsync(newAgentVersion.Value.Name);
        Console.WriteLine($"Deleted agent with name: {newAgentVersion.Value.Name}");
        await _projectClient.ProjectOpenAIClient.GetProjectVectorStoresClient().DeleteVectorStoreAsync(vectorStore.Value.Id);
        Console.WriteLine($"Deleted VectorStore with ID: {vectorStore.Value.Id}");
        await _projectClient.ProjectOpenAIClient.GetProjectFilesClient().DeleteFileAsync(uploadedFile.Value.Id);
        Console.WriteLine($"Deleted file with ID: {uploadedFile.Value.Id}");

        return response.GetOutputText();
    }

    public async Task<string> AnalyzeDocumentAsync(BinaryData documentBytes, string fileName, string documentMimeType, string expectedJsonSchema, string userInstructions)
    {
        _logger.LogInformation("Starting AnalyzeDocumentAsync");
        var prompt = @$"
                        Analyse le document « {fileName} », de type {documentMimeType}.
                        Utilise l’outil file_search pour rechercher les informations nécessaires.
                        Appuie ta réponse sur le contenu du document.
                        Si les informations sont insuffisantes, indique-le clairement.

                        Question ou demande de l’utilisateur :
                        {userInstructions}

                        Réponds exclusivement en français.
                        Respecte le schéma JSON suivant, en conservant ses clés techniques
                        et en rédigeant les textes destinés à l’utilisateur en français :
                        {expectedJsonSchema}";

        CreateResponseOptions options = new()
        {
            InputItems =
            {
                ResponseItem.CreateUserMessageItem(
                [
                    ResponseContentPart.CreateInputTextPart(prompt),
                    ResponseContentPart.CreateInputFilePart(documentBytes, documentMimeType, fileName)
                ])
            },
        };

        // Chat with the agent to answer questions
        ResponseResult response = await _baseResponseClient.CreateResponseAsync(options);
        _logger.LogInformation("AnalyzeDocumentAsync completed");

        return response.GetOutputText();
    }

    public async Task<string> AnalyzeImageAsync(BinaryData documentBytes, string fileName, string documentMimeType, string expectedJsonSchema, string userInstructions)
    {
        _logger.LogInformation("Starting AnalyzeImageAsync");
        var prompt = @$"You are a helpful assistant that can analyze the content of the image provided in the input and answer questions about it.
                The image is called {fileName} and is in the following format: {documentMimeType}.
                Here are the user instructions for analyzing the image: {userInstructions}.
                Please provide your answer in a JSON format that adheres to the following schema: {expectedJsonSchema}";
        CreateResponseOptions options = new()
        {
            InputItems =
            {
                ResponseItem.CreateUserMessageItem(
                [
                    ResponseContentPart.CreateInputTextPart(prompt),
                    ResponseContentPart.CreateInputImagePart(documentBytes)
                ])
            },
        };

        // Chat with the agent to answer questions
        ResponseResult response = await _baseResponseClient.CreateResponseAsync(options);
        _logger.LogInformation("AnalyzeImageAsync completed");

        return response.GetOutputText();
    }
}