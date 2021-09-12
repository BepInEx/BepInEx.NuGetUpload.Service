FROM mcr.microsoft.com/dotnet/aspnet:5.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:5.0 AS build
WORKDIR /src
COPY ["NuGetUpload/NuGetUpload.csproj", "NuGetUpload/"]
RUN dotnet restore "NuGetUpload/NuGetUpload.csproj"
COPY . .
WORKDIR "/src/NuGetUpload"
RUN dotnet build "NuGetUpload.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "NuGetUpload.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "NuGetUpload.dll"]
