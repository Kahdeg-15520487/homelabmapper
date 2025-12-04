# HomelabMapper Docker Setup

## Quick Start

1. **Copy environment file:**
   ```bash
   cp .env.example .env
   ```

2. **Edit `.env` with your credentials:**
   ```bash
   nano .env
   ```

3. **Create output directory:**
   ```bash
   mkdir -p output
   ```

4. **Update `config.yaml` with your network settings**

5. **Build and run:**
   ```bash
   docker-compose up -d
   ```

6. **Run a scan:**
   ```bash
   docker-compose run --rm homelabmapper
   ```

## Configuration

### Network Mode

The service uses `network_mode: host` to allow scanning your local network. If you need to use a different network mode, you'll need to adjust the subnet scanning accordingly.

### Volumes

- `./config.yaml` - Configuration file (read-only)
- `./output` - Output directory for reports (scan-results.json, report.md, topology.mmd, changes.md)
- `./.homelabmapper` - Scan history directory for diff tracking

### Environment Variables

Set these in your `.env` file:

- `PROXMOX_TOKEN` - Proxmox API token
- `PORTAINER_TOKEN` - Portainer API token
- `DOCKER_TOKEN` - Docker API token (optional)
- `UNRAID_API_KEY` - Unraid API key (optional)
- `ROUTER_PASSWORD` - Router admin password
- `SSH_PASSWORD` - SSH password (optional)
- `TZ` - Timezone (default: UTC)

## Usage

### One-time Scan

```bash
docker-compose run --rm homelabmapper
```

### Scheduled Scans

Edit `docker-compose.yml` and uncomment the `command` line to run scans on a schedule:

```yaml
command: sh -c "while true; do dotnet HomelabMapper.CLI.dll /app/config/config.yaml && sleep 3600; done"
```

This runs a scan every hour (3600 seconds). Adjust the sleep duration as needed.

### View Reports via Web Interface

Start the optional nginx web server:

```bash
docker-compose --profile web up -d
```

Then access reports at: http://localhost:8080/report.md

## Building

### Build locally:
```bash
docker-compose build
```

### Build with specific tag:
```bash
docker build -t homelabmapper:latest .
```

## Troubleshooting

### Chrome/Puppeteer Issues

If the router scanner fails with Chrome-related errors, ensure the Dockerfile has all required dependencies installed.

### Network Scanning Issues

If hosts aren't being discovered:
- Verify `network_mode: host` is set
- Check that your subnets are correctly configured in `config.yaml`
- Ensure the container has network access to your homelab devices

### Permission Issues

If you get permission errors with output files:
```bash
sudo chown -R $USER:$USER output .homelabmapper
```

## Advanced Configuration

### Custom Chrome Path

If you need to use a different Chrome installation, modify the Dockerfile to install Chrome in a different location and update the RouterF670YClient.cs accordingly.

### Resource Limits

Add resource limits to docker-compose.yml:

```yaml
services:
  homelabmapper:
    # ... other config ...
    deploy:
      resources:
        limits:
          cpus: '1'
          memory: 1G
        reservations:
          cpus: '0.5'
          memory: 512M
```

## Updates

To update to the latest version:

```bash
docker-compose down
docker-compose pull  # if using pre-built image
# or
docker-compose build  # if building locally
docker-compose up -d
```
