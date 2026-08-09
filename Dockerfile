# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY LabbyTwo.csproj .
RUN dotnet restore LabbyTwo.csproj
COPY . .
ARG LABBYTWO_VERSION=dev
# Named explicitly: the repository also holds a solution and a test project, and an
# unqualified publish would not know which of them to build.
RUN dotnet publish LabbyTwo.csproj -c Release -o /app /p:InformationalVersion=$LABBYTWO_VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# The database, the data-protection keyring, plugin DLLs, and therefore every stored
# credential live here. Mount it or a container rebuild forgets the entire configuration.
VOLUME /app/data

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Bash, not sh: /bin/sh in this image is dash, which has no /dev/tcp, and the aspnet
# image ships neither curl nor wget. Bash is present, so this needs no extra packages.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["/bin/bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /healthz HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3 && grep -q '200 OK' <&3"]

ENTRYPOINT ["dotnet", "LabbyTwo.dll"]
