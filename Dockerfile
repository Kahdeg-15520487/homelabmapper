FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and project files
COPY HomelabMapper.sln ./
COPY src/HomelabMapper.CLI/HomelabMapper.CLI.csproj ./src/HomelabMapper.CLI/
COPY src/HomelabMapper.Core/HomelabMapper.Core.csproj ./src/HomelabMapper.Core/
COPY src/HomelabMapper.Correlation/HomelabMapper.Correlation.csproj ./src/HomelabMapper.Correlation/
COPY src/HomelabMapper.Detectors/HomelabMapper.Detectors.csproj ./src/HomelabMapper.Detectors/
COPY src/HomelabMapper.Discovery/HomelabMapper.Discovery.csproj ./src/HomelabMapper.Discovery/
COPY src/HomelabMapper.Integration/HomelabMapper.Integration.csproj ./src/HomelabMapper.Integration/
COPY src/HomelabMapper.Reporting/HomelabMapper.Reporting.csproj ./src/HomelabMapper.Reporting/

# Restore dependencies
RUN dotnet restore

# Copy source code
COPY src/ ./src/

# Build and publish
RUN dotnet publish src/HomelabMapper.CLI/HomelabMapper.CLI.csproj -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

# Install Chrome for PuppeteerSharp
RUN apt-get update && apt-get install -y \
    wget \
    gnupg \
    ca-certificates \
    fonts-liberation \
    libnss3 \
    libatk-bridge2.0-0 \
    libx11-xcb1 \
    libxcomposite1 \
    libxdamage1 \
    libxrandr2 \
    libgbm1 \
    libasound2 \
    libpangocairo-1.0-0 \
    libgtk-3-0 \
    && wget -q -O - https://dl-ssl.google.com/linux/linux_signing_key.pub | apt-key add - \
    && echo "deb [arch=amd64] http://dl.google.com/linux/chrome/deb/ stable main" >> /etc/apt/sources.list.d/google-chrome.list \
    && apt-get update \
    && apt-get install -y google-chrome-stable \
    && rm -rf /var/lib/apt/lists/*

# Copy published app
COPY --from=build /app/publish .

# Copy Docker-optimized config template
COPY config.docker.yaml ./config.yaml

# Create output directory
RUN mkdir -p /app/output /app/.homelabmapper/scans

# Create volume mount points
VOLUME ["/app/output", "/app/.homelabmapper"]

# Set environment variables
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    CHROME_PATH=/usr/bin/google-chrome-stable

ENTRYPOINT ["dotnet", "HomelabMapper.CLI.dll"]
CMD ["config.yaml"]
