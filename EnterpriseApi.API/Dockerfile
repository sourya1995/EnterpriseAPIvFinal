FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["EnterpriseApi.Domain/EnterpriseApi.Domain.csproj", "EnterpriseApi.Domain/"]
COPY ["EnterpriseApi.Application/EnterpriseApi.Application.csproj", "EnterpriseApi.Application/"]
COPY ["EnterpriseApi.Infrastructure/EnterpriseApi.Infrastructure.csproj", "EnterpriseApi.Infrastructure/"]
COPY ["EnterpriseApi.API/EnterpriseApi.API.csproj", "EnterpriseApi.API/"]

RUN dotnet restore "EnterpriseApi.API/EnterpriseApi.API.csproj"

COPY . .
WORKDIR "/src/EnterpriseApi.API"
RUN dotnet build "EnterpriseApi.API.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "EnterpriseApi.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
RUN mkdir -p /app/data
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "EnterpriseApi.API.dll"]
