FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files for restore
COPY Elwood.Core/Elwood.Core.csproj Elwood.Core/
COPY Elwood.Json/Elwood.Json.csproj Elwood.Json/
COPY Elwood.Api/Elwood.Api.csproj Elwood.Api/
RUN dotnet restore Elwood.Api/Elwood.Api.csproj

# Copy source and publish
COPY . .
RUN dotnet publish Elwood.Api/Elwood.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Elwood.Api.dll"]
