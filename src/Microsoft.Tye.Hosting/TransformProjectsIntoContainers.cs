// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Tye.Hosting.Model;

namespace Microsoft.Tye.Hosting
{
    public class TransformProjectsIntoContainers : IApplicationProcessor
    {
        private readonly ILogger _logger;

        public TransformProjectsIntoContainers(ILogger logger)
        {
            _logger = logger;
        }

        public Task StartAsync(Application application)
        {
            // This transforms a ProjectRunInfo into a container
            var tasks = new List<Task>();
            foreach (var s in application.Services.Values)
            {
                if (s.Description.RunInfo is ProjectRunInfo project)
                {
                    tasks.Add(TransformProjectToContainer(s, project));
                }
                else if (s.Description.RunInfo is IngressRunInfo ingress)
                {
                    tasks.Add(TransformIngressToContainer(application, s, ingress));
                }
            }

            return Task.WhenAll(tasks);
        }

        private Task TransformIngressToContainer(Application application, Service service, IngressRunInfo ingress)
        {
            // Inject a proxy per non-container service. This allows the container to use normal host names within the
            // container network to talk to services on the host
            var ingressRunInfo = new DockerRunInfo($"mcr.microsoft.com/dotnet/core/sdk:3.1", "dotnet Microsoft.Tye.HttpProxy.dll")
            {
                WorkingDirectory = "/app",
            };

            var proxyLocation = Path.GetDirectoryName(typeof(Microsoft.Tye.HttpProxy.Program).Assembly.Location);
            ingressRunInfo.VolumeMappings.Add(new DockerVolume(proxyLocation, name: null, target: "/app"));

            int ruleIndex = 0;
            foreach (var rule in ingress.Rules)
            {
                if (!application.Services.TryGetValue(rule.Service, out var target))
                {
                    continue;
                }

                var targetServiceDescription = target.Description;

                var uris = new List<Uri>();

                // HTTP before HTTPS (this might change once we figure out certs...)
                var targetBinding = targetServiceDescription.Bindings.FirstOrDefault(b => b.Protocol == "http") ??
                                    targetServiceDescription.Bindings.FirstOrDefault(b => b.Protocol == "https");

                if (targetBinding == null)
                {
                    _logger.LogInformation("Service {ServiceName} does not have any HTTP or HTTPs bindings", targetServiceDescription.Name);
                    continue;
                }

                // For each of the target service replicas, get the base URL
                // based on the replica port
                foreach (var port in targetBinding.ReplicaPorts)
                {
                    var url = $"{targetBinding.Protocol}://localhost:{port}";
                    uris.Add(new Uri(url));
                }

                // TODO: Use Yarp
                // Configuration schema
                //    "Rules": 
                //    {
                //         "0": 
                //         {
                //           "Host": null,
                //           "Path": null,
                //           "Service": "frontend",
                //           "Port": 10067",
                //           "Protocol": http
                //         }
                //    }

                var rulePrefix = $"Rules__{ruleIndex}__";

                service.Description.Configuration.Add(new EnvironmentVariable($"{rulePrefix}Host", rule.Host));
                service.Description.Configuration.Add(new EnvironmentVariable($"{rulePrefix}Path", rule.Path));
                service.Description.Configuration.Add(new EnvironmentVariable($"{rulePrefix}Service", rule.Service));
                service.Description.Configuration.Add(new EnvironmentVariable($"{rulePrefix}Port", (targetBinding.ContainerPort ?? targetBinding.Port).ToString()));
                service.Description.Configuration.Add(new EnvironmentVariable($"{rulePrefix}Protocol", targetBinding.Protocol));

                ruleIndex++;
            }


            service.Description.RunInfo = ingressRunInfo;

            return Task.CompletedTask;
        }

        private async Task TransformProjectToContainer(Service service, ProjectRunInfo project)
        {
            var serviceDescription = service.Description;
            var serviceName = serviceDescription.Name;

            service.Status.ProjectFilePath = project.ProjectFile.FullName;
            var targetFramework = project.TargetFramework;

            // Sometimes building can fail because of file locking (like files being open in VS)
            _logger.LogInformation("Publishing project {ProjectFile}", service.Status.ProjectFilePath);

            var buildArgs = project.BuildProperties.Aggregate(string.Empty, (current, property) => current + $" /p:{property.Key}={property.Value}").TrimStart();

            var publishCommand = $"publish \"{service.Status.ProjectFilePath}\" --framework {targetFramework} {buildArgs} /nologo";

            service.Logs.OnNext($"dotnet {publishCommand}");

            var buildResult = await ProcessUtil.RunAsync("dotnet", publishCommand, throwOnError: false);

            service.Logs.OnNext(buildResult.StandardOutput);

            if (buildResult.ExitCode != 0)
            {
                _logger.LogInformation("Publishing {ProjectFile} failed with exit code {ExitCode}: \r\n" + buildResult.StandardOutput, service.Status.ProjectFilePath, buildResult.ExitCode);

                // Null out the RunInfo so that
                serviceDescription.RunInfo = null;
                return;
            }

            // We transform the project information into the following docker command:
            // docker run -w /app -v {publishDir}:/app -it {image} dotnet {outputfile}.dll

            var containerImage = DetermineContainerImage(project);
            var outputFileName = project.AssemblyName + ".dll";
            var dockerRunInfo = new DockerRunInfo(containerImage, $"dotnet {outputFileName} {project.Args}")
            {
                WorkingDirectory = "/app",
                Private = project.Private
            };

            dockerRunInfo.VolumeMappings.Add(new DockerVolume(source: project.PublishOutputPath, name: null, target: "/app"));

            // Make volume mapping works when running as a container
            dockerRunInfo.VolumeMappings.AddRange(project.VolumeMappings);

            // Change the project into a container info
            serviceDescription.RunInfo = dockerRunInfo;
        }

        private static string DetermineContainerImage(ProjectRunInfo project)
        {
            return $"mcr.microsoft.com/dotnet/core/sdk:{project.TargetFrameworkVersion}";
        }

        public Task StopAsync(Application application)
        {
            return Task.CompletedTask;
        }
    }
}
