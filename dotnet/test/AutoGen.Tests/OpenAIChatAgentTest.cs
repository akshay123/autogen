﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// OpenAIChatAgentTest.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoGen.OpenAI;
using Azure.AI.OpenAI;
using FluentAssertions;

namespace AutoGen.Tests;

public partial class OpenAIChatAgentTest
{
    /// <summary>
    /// Get the weather for a location.
    /// </summary>
    /// <param name="location">location</param>
    /// <returns></returns>
    [Function]
    public async Task<string> GetWeatherAsync(string location)
    {
        return $"The weather in {location} is sunny.";
    }

    [ApiKeyFact("AZURE_OPENAI_API_KEY", "AZURE_OPENAI_ENDPOINT")]
    public async Task BasicConversationTestAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? throw new Exception("Please set AZURE_OPENAI_ENDPOINT environment variable.");
        var key = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? throw new Exception("Please set AZURE_OPENAI_API_KEY environment variable.");
        var openaiClient = new OpenAIClient(new Uri(endpoint), new Azure.AzureKeyCredential(key));
        var openAIChatAgent = new OpenAIChatAgent(
            openAIClient: openaiClient,
            name: "assistant",
            modelName: "gpt-35-turbo-16k");

        // By default, OpenAIChatClient supports the following message types
        // - IMessage<ChatRequestMessage>
        var chatMessageContent = MessageEnvelope.Create(new ChatRequestUserMessage("Hello"));
        var reply = await openAIChatAgent.SendAsync(chatMessageContent);

        reply.Should().BeOfType<MessageEnvelope<ChatResponseMessage>>();
        reply.As<MessageEnvelope<ChatResponseMessage>>().From.Should().Be("assistant");
        reply.As<MessageEnvelope<ChatResponseMessage>>().Content.Role.Should().Be(ChatRole.Assistant);

        // test streaming
        var streamingReply = await openAIChatAgent.GenerateStreamingReplyAsync(new[] { chatMessageContent });

        await foreach (var streamingMessage in streamingReply)
        {
            streamingMessage.Should().BeOfType<MessageEnvelope<StreamingChatCompletionsUpdate>>();
            streamingMessage.As<MessageEnvelope<StreamingChatCompletionsUpdate>>().From.Should().Be("assistant");
        }
    }

    [ApiKeyFact("AZURE_OPENAI_API_KEY", "AZURE_OPENAI_ENDPOINT")]
    public async Task OpenAIChatMessageContentConnectorTestAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? throw new Exception("Please set AZURE_OPENAI_ENDPOINT environment variable.");
        var key = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? throw new Exception("Please set AZURE_OPENAI_API_KEY environment variable.");
        var openaiClient = new OpenAIClient(new Uri(endpoint), new Azure.AzureKeyCredential(key));
        var openAIChatAgent = new OpenAIChatAgent(
            openAIClient: openaiClient,
            name: "assistant",
            modelName: "gpt-35-turbo-16k");

        var openAIChatMessageConnector = new OpenAIChatRequestMessageConnector();
        MiddlewareStreamingAgent<OpenAIChatAgent> assistant = openAIChatAgent
            .RegisterStreamingMiddleware(openAIChatMessageConnector)
            .RegisterMiddleware(openAIChatMessageConnector);

        var messages = new IMessage[]
        {
            MessageEnvelope.Create(new ChatRequestUserMessage("Hello")),
            new TextMessage(Role.Assistant, "Hello", from: "user"),
            new MultiModalMessage(Role.Assistant,
                [
                    new TextMessage(Role.Assistant, "Hello", from: "user"),
                ],
                from: "user"),
            new Message(Role.Assistant, "Hello", from: "user"), // Message type is going to be deprecated, please use TextMessage instead
        };

        foreach (var message in messages)
        {
            var reply = await assistant.SendAsync(message);

            reply.Should().BeOfType<TextMessage>();
            reply.As<TextMessage>().From.Should().Be("assistant");
        }

        // test streaming
        foreach (var message in messages)
        {
            var reply = await assistant.GenerateStreamingReplyAsync([message]);

            await foreach (var streamingMessage in reply)
            {
                streamingMessage.Should().BeOfType<TextMessageUpdate>();
                streamingMessage.As<TextMessageUpdate>().From.Should().Be("assistant");
            }
        }
    }

    [ApiKeyFact("AZURE_OPENAI_API_KEY", "AZURE_OPENAI_ENDPOINT")]
    public async Task OpenAIChatAgentToolCallTestAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? throw new Exception("Please set AZURE_OPENAI_ENDPOINT environment variable.");
        var key = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? throw new Exception("Please set AZURE_OPENAI_API_KEY environment variable.");
        var openaiClient = new OpenAIClient(new Uri(endpoint), new Azure.AzureKeyCredential(key));
        var openAIChatAgent = new OpenAIChatAgent(
            openAIClient: openaiClient,
            name: "assistant",
            modelName: "gpt-35-turbo-16k");

        var openAIChatMessageConnector = new OpenAIChatRequestMessageConnector();
        var functionCallMiddleware = new FunctionCallMiddleware(
            functions: [this.GetWeatherAsyncFunctionContract]);
        MiddlewareStreamingAgent<OpenAIChatAgent> assistant = openAIChatAgent
            .RegisterStreamingMiddleware(openAIChatMessageConnector)
            .RegisterMiddleware(openAIChatMessageConnector);

        var functionCallAgent = assistant
            .RegisterMiddleware(functionCallMiddleware)
            .RegisterStreamingMiddleware(functionCallMiddleware);

        var question = "What's the weather in Seattle";
        var messages = new IMessage[]
        {
            MessageEnvelope.Create(new ChatRequestUserMessage(question)),
            new TextMessage(Role.Assistant, question, from: "user"),
            new MultiModalMessage(Role.Assistant,
                [
                    new TextMessage(Role.Assistant, question, from: "user"),
                ],
                from: "user"),
            new Message(Role.Assistant, question, from: "user"), // Message type is going to be deprecated, please use TextMessage instead
        };

        foreach (var message in messages)
        {
            var reply = await functionCallAgent.SendAsync(message);

            reply.Should().BeOfType<ToolCallMessage>();
            reply.As<ToolCallMessage>().From.Should().Be("assistant");
            reply.As<ToolCallMessage>().ToolCalls.Count().Should().Be(1);
            reply.As<ToolCallMessage>().ToolCalls.First().FunctionName.Should().Be(this.GetWeatherAsyncFunctionContract.Name);
        }

        // test streaming
        foreach (var message in messages)
        {
            var reply = await functionCallAgent.GenerateStreamingReplyAsync([message]);
            ToolCallMessage? toolCallMessage = null;
            await foreach (var streamingMessage in reply)
            {
                streamingMessage.Should().BeOfType<ToolCallMessageUpdate>();
                streamingMessage.As<ToolCallMessageUpdate>().From.Should().Be("assistant");
                if (toolCallMessage is null)
                {
                    toolCallMessage = new ToolCallMessage(streamingMessage.As<ToolCallMessageUpdate>());
                }
                else
                {
                    toolCallMessage.Update(streamingMessage.As<ToolCallMessageUpdate>());
                }
            }

            toolCallMessage.Should().NotBeNull();
            toolCallMessage!.From.Should().Be("assistant");
            toolCallMessage.ToolCalls.Count().Should().Be(1);
            toolCallMessage.ToolCalls.First().FunctionName.Should().Be(this.GetWeatherAsyncFunctionContract.Name);
        }
    }

    [ApiKeyFact("AZURE_OPENAI_API_KEY", "AZURE_OPENAI_ENDPOINT")]
    public async Task OpenAIChatAgentToolCallInvokingTestAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? throw new Exception("Please set AZURE_OPENAI_ENDPOINT environment variable.");
        var key = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? throw new Exception("Please set AZURE_OPENAI_API_KEY environment variable.");
        var openaiClient = new OpenAIClient(new Uri(endpoint), new Azure.AzureKeyCredential(key));
        var openAIChatAgent = new OpenAIChatAgent(
            openAIClient: openaiClient,
            name: "assistant",
            modelName: "gpt-35-turbo-16k");

        var openAIChatMessageConnector = new OpenAIChatRequestMessageConnector();
        var functionCallMiddleware = new FunctionCallMiddleware(
            functions: [this.GetWeatherAsyncFunctionContract],
            functionMap: new Dictionary<string, Func<string, Task<string>>> { { this.GetWeatherAsyncFunctionContract.Name!, this.GetWeatherAsyncWrapper } });
        MiddlewareStreamingAgent<OpenAIChatAgent> assistant = openAIChatAgent
            .RegisterStreamingMiddleware(openAIChatMessageConnector)
            .RegisterMiddleware(openAIChatMessageConnector);

        var functionCallAgent = assistant
            .RegisterMiddleware(functionCallMiddleware)
            .RegisterStreamingMiddleware(functionCallMiddleware);

        var question = "What's the weather in Seattle";
        var messages = new IMessage[]
        {
            MessageEnvelope.Create(new ChatRequestUserMessage(question)),
            new TextMessage(Role.Assistant, question, from: "user"),
            new MultiModalMessage(Role.Assistant,
                [
                    new TextMessage(Role.Assistant, question, from: "user"),
                ],
                from: "user"),
            new Message(Role.Assistant, question, from: "user"), // Message type is going to be deprecated, please use TextMessage instead
        };

        foreach (var message in messages)
        {
            var reply = await functionCallAgent.SendAsync(message);

            reply.Should().BeOfType<AggregateMessage<ToolCallMessage, ToolCallResultMessage>>();
            reply.From.Should().Be("assistant");
            reply.GetToolCalls()!.Count().Should().Be(1);
            reply.GetToolCalls()!.First().FunctionName.Should().Be(this.GetWeatherAsyncFunctionContract.Name);
            reply.GetContent()!.ToLower().Should().Contain("seattle");
        }

        // test streaming
        foreach (var message in messages)
        {
            var reply = await functionCallAgent.GenerateStreamingReplyAsync([message]);
            await foreach (var streamingMessage in reply)
            {
                if (streamingMessage is not IMessage)
                {
                    streamingMessage.Should().BeOfType<ToolCallMessageUpdate>();
                    streamingMessage.As<ToolCallMessageUpdate>().From.Should().Be("assistant");
                }
                else
                {
                    streamingMessage.Should().BeOfType<AggregateMessage<ToolCallMessage, ToolCallResultMessage>>();
                    streamingMessage.As<IMessage>().GetContent()!.ToLower().Should().Contain("seattle");
                }
            }
        }
    }
}
