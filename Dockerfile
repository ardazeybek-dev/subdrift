# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Subdrift.Cli -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /work
COPY --from=build /app /app
ENTRYPOINT ["dotnet", "/app/subdrift.dll"]
CMD ["--help"]
