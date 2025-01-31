﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// SingleAgentTest.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoGen.OpenAI;
using Azure.AI.OpenAI;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace AutoGen.Tests
{
    public partial class SingleAgentTest
    {
        private ITestOutputHelper _output;
        public SingleAgentTest(ITestOutputHelper output)
        {
            _output = output;
        }

        private ILLMConfig CreateAzureOpenAIGPT35TurboConfig()
        {
            var key = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? throw new ArgumentException("AZURE_OPENAI_API_KEY is not set");
            var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? throw new ArgumentException("AZURE_OPENAI_ENDPOINT is not set");
            return new AzureOpenAIConfig(endpoint, "gpt-35-turbo-16k", key);
        }

        private ILLMConfig CreateOpenAIGPT4VisionConfig()
        {
            var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw new ArgumentException("OPENAI_API_KEY is not set");
            return new OpenAIConfig(key, "gpt-4-vision-preview");
        }

        [ApiKeyFact("AZURE_OPENAI_API_KEY", "AZURE_OPENAI_ENDPOINT")]
        public async Task GPTAgentTestAsync()
        {
            var config = this.CreateAzureOpenAIGPT35TurboConfig();

            var agent = new GPTAgent("gpt", "You are a helpful AI assistant", config);

            await UpperCaseTest(agent);
            await UpperCaseStreamingTestAsync(agent);
        }

        [ApiKeyFact("OPENAI_API_KEY", "AZURE_OPENAI_API_KEY", "AZURE_OPENAI_ENDPOINT")]
        public async Task GPTAgentVisionTestAsync()
        {
            var visionConfig = this.CreateOpenAIGPT4VisionConfig();
            var visionAgent = new GPTAgent(
                name: "gpt",
                systemMessage: "You are a helpful AI assistant",
                config: visionConfig,
                temperature: 0);

            var gpt3Config = this.CreateAzureOpenAIGPT35TurboConfig();
            var gpt3Agent = new GPTAgent(
                name: "gpt3",
                systemMessage: "You are a helpful AI assistant, return highest label from conversation",
                config: gpt3Config,
                temperature: 0,
                functions: new[] { this.GetHighestLabelFunction },
                functionMap: new Dictionary<string, Func<string, Task<string>>>
                {
                    { nameof(GetHighestLabel), this.GetHighestLabelWrapper },
                });

            var imageUri = new Uri(@"https://microsoft.github.io/autogen/assets/images/level2algebra-659ba95286432d9945fc89e84d606797.png");
            var oaiMessage = new ChatRequestUserMessage(
                new ChatMessageTextContentItem("which label has the highest inference cost"),
                new ChatMessageImageContentItem(imageUri));
            var multiModalMessage = new MultiModalMessage(Role.User,
                [
                    new TextMessage(Role.User, "which label has the highest inference cost", from: "user"),
                    new ImageMessage(Role.User, imageUri, from: "user"),
                ],
                from: "user");

            var imageMessage = new ImageMessage(Role.User, imageUri, from: "user");

            IMessage[] messages = [
                MessageEnvelope.Create(oaiMessage),
                multiModalMessage,
                imageMessage,
                ];
            foreach (var message in messages)
            {
                var response = await visionAgent.SendAsync(message);
                response.From.Should().Be(visionAgent.Name);

                var labelResponse = await gpt3Agent.SendAsync(response);
                labelResponse.From.Should().Be(gpt3Agent.Name);
                labelResponse.GetToolCalls()!.First().FunctionName.Should().Be(nameof(GetHighestLabel));
            }
        }

        [ApiKeyFact("AZURE_OPENAI_API_KEY", "AZURE_OPENAI_ENDPOINT")]
        public async Task GPTFunctionCallAgentTestAsync()
        {
            var config = this.CreateAzureOpenAIGPT35TurboConfig();
            var agentWithFunction = new GPTAgent("gpt", "You are a helpful AI assistant", config, 0, functions: new[] { this.EchoAsyncFunction });

            await EchoFunctionCallTestAsync(agentWithFunction);
            await UpperCaseTest(agentWithFunction);
        }

        [ApiKeyFact("AZURE_OPENAI_API_KEY", "AZURE_OPENAI_ENDPOINT")]
        public async Task AssistantAgentFunctionCallTestAsync()
        {
            var config = this.CreateAzureOpenAIGPT35TurboConfig();

            var llmConfig = new ConversableAgentConfig
            {
                Temperature = 0,
                FunctionContracts = new[]
                {
                    this.EchoAsyncFunctionContract,
                },
                ConfigList = new[]
                {
                    config,
                },
            };

            var assistantAgent = new AssistantAgent(
                name: "assistant",
                llmConfig: llmConfig);

            await EchoFunctionCallTestAsync(assistantAgent);
            await UpperCaseTest(assistantAgent);
        }


        [Fact]
        public async Task AssistantAgentDefaultReplyTestAsync()
        {
            var assistantAgent = new AssistantAgent(
                llmConfig: null,
                name: "assistant",
                defaultReply: "hello world");

            var reply = await assistantAgent.SendAsync("hi");

            reply.GetContent().Should().Be("hello world");
            reply.GetRole().Should().Be(Role.Assistant);
            reply.From.Should().Be(assistantAgent.Name);
        }

        [ApiKeyFact("AZURE_OPENAI_API_KEY", "AZURE_OPENAI_ENDPOINT")]
        public async Task AssistantAgentFunctionCallSelfExecutionTestAsync()
        {
            var config = this.CreateAzureOpenAIGPT35TurboConfig();
            var llmConfig = new ConversableAgentConfig
            {
                FunctionContracts = new[]
                {
                    this.EchoAsyncFunctionContract,
                },
                ConfigList = new[]
                {
                    config,
                },
            };
            var assistantAgent = new AssistantAgent(
                name: "assistant",
                llmConfig: llmConfig,
                functionMap: new Dictionary<string, Func<string, Task<string>>>
                {
                    { nameof(EchoAsync), this.EchoAsyncWrapper },
                });

            await EchoFunctionCallExecutionTestAsync(assistantAgent);
            await UpperCaseTest(assistantAgent);
        }

        [ApiKeyFact("AZURE_OPENAI_API_KEY", "AZURE_OPENAI_ENDPOINT")]
        public async Task GPTAgentFunctionCallSelfExecutionTestAsync()
        {
            var config = this.CreateAzureOpenAIGPT35TurboConfig();
            var agent = new GPTAgent(
                name: "gpt",
                systemMessage: "You are a helpful AI assistant",
                config: config,
                temperature: 0,
                functions: new[] { this.EchoAsyncFunction },
                functionMap: new Dictionary<string, Func<string, Task<string>>>
                {
                    { nameof(EchoAsync), this.EchoAsyncWrapper },
                });

            await EchoFunctionCallExecutionStreamingTestAsync(agent);
            await EchoFunctionCallExecutionTestAsync(agent);
            await UpperCaseTest(agent);
        }

        /// <summary>
        /// echo when asked.
        /// </summary>
        /// <param name="message">message to echo</param>
        [FunctionAttribute]
        public async Task<string> EchoAsync(string message)
        {
            return $"[ECHO] {message}";
        }

        /// <summary>
        /// return the label name with hightest inference cost
        /// </summary>
        /// <param name="labelName"></param>
        /// <returns></returns>
        [FunctionAttribute]
        public async Task<string> GetHighestLabel(string labelName, string color)
        {
            return $"[HIGHEST_LABEL] {labelName} {color}";
        }

        private async Task EchoFunctionCallTestAsync(IAgent agent)
        {
            var message = new TextMessage(Role.System, "You are a helpful AI assistant that call echo function");
            var helloWorld = new TextMessage(Role.User, "echo Hello world");

            var reply = await agent.SendAsync(chatHistory: new[] { message, helloWorld });

            reply.From.Should().Be(agent.Name);
            reply.GetToolCalls()!.First().FunctionName.Should().Be(nameof(EchoAsync));
        }

        private async Task EchoFunctionCallExecutionTestAsync(IAgent agent)
        {
            var message = new TextMessage(Role.System, "You are a helpful AI assistant that echo whatever user says");
            var helloWorld = new TextMessage(Role.User, "echo Hello world");

            var reply = await agent.SendAsync(chatHistory: new[] { message, helloWorld });

            reply.GetContent().Should().Be("[ECHO] Hello world");
            reply.From.Should().Be(agent.Name);
            reply.Should().BeOfType<AggregateMessage<ToolCallMessage, ToolCallResultMessage>>();
        }

        private async Task EchoFunctionCallExecutionStreamingTestAsync(IStreamingAgent agent)
        {
            var message = new TextMessage(Role.System, "You are a helpful AI assistant that echo whatever user says");
            var helloWorld = new TextMessage(Role.User, "echo Hello world");
            var option = new GenerateReplyOptions
            {
                Temperature = 0,
            };
            var replyStream = await agent.GenerateStreamingReplyAsync(messages: new[] { message, helloWorld }, option);
            var answer = "[ECHO] Hello world";
            IStreamingMessage? finalReply = default;
            await foreach (var reply in replyStream)
            {
                reply.From.Should().Be(agent.Name);
                finalReply = reply;
            }

            if (finalReply is AggregateMessage<ToolCallMessage, ToolCallResultMessage> aggregateMessage)
            {
                var toolCallResultMessage = aggregateMessage.Message2;
                toolCallResultMessage.ToolCalls.First().Result.Should().Be(answer);
                toolCallResultMessage.From.Should().Be(agent.Name);
                toolCallResultMessage.ToolCalls.First().FunctionName.Should().Be(nameof(EchoAsync));
            }
            else
            {
                throw new Exception("unexpected message type");
            }
        }

        private async Task UpperCaseTest(IAgent agent)
        {
            var message = new TextMessage(Role.System, "You are a helpful AI assistant that convert user message to upper case");
            var uppCaseMessage = new TextMessage(Role.User, "abcdefg");

            var reply = await agent.SendAsync(chatHistory: new[] { message, uppCaseMessage });

            reply.GetContent().Should().Be("ABCDEFG");
            reply.From.Should().Be(agent.Name);
        }

        private async Task UpperCaseStreamingTestAsync(IStreamingAgent agent)
        {
            var message = new TextMessage(Role.System, "You are a helpful AI assistant that convert user message to upper case");
            var helloWorld = new TextMessage(Role.User, "a b c d e f g h i j k l m n");
            var option = new GenerateReplyOptions
            {
                Temperature = 0,
            };
            var replyStream = await agent.GenerateStreamingReplyAsync(messages: new[] { message, helloWorld }, option);
            var answer = "A B C D E F G H I J K L M N";
            TextMessage? finalReply = default;
            await foreach (var reply in replyStream)
            {
                if (reply is TextMessageUpdate update)
                {
                    update.From.Should().Be(agent.Name);

                    if (finalReply is null)
                    {
                        finalReply = new TextMessage(update);
                    }
                    else
                    {
                        finalReply.Update(update);
                    }

                    continue;
                }

                throw new Exception("unexpected message type");
            }

            finalReply!.Content.Should().Be(answer);
            finalReply!.Role.Should().Be(Role.Assistant);
            finalReply!.From.Should().Be(agent.Name);
        }
    }
}
