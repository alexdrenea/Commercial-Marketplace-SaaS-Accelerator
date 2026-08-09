# Copyright (c) Microsoft Corporation. All rights reserved.
# Licensed under the MIT License. See LICENSE file in the project root for license information.

#
# Powershell script to deploy the resources - Customer portal, Publisher portal and the Azure SQL Database
#

#.\Deploy.ps1 `
# -WebAppNamePrefix "amp_saas_accelerator_<unique>" `
# -Location "<region>" `
# -PublisherAdminUsers "<your@email.address>"

Param(  
   [string][Parameter(Mandatory)]$WebAppNamePrefix, # Prefix used for creating web applications
   [string][Parameter()]$ResourceGroupForDeployment, # Name of the resource group to deploy the resources
   [string][Parameter(Mandatory)]$Location, # Location of the resource group
   [string][Parameter(Mandatory)]$PublisherAdminUsers, # Provide a list of email addresses (as comma-separated-values) that should be granted access to the Publisher Portal
   [string][Parameter()]$TenantID, # The value should match the value provided for Active Directory TenantID in the Technical Configuration of the Transactable Offer in Partner Center
   [string][Parameter()]$AzureSubscriptionID, # Subscription where the resources be deployed
   [string][Parameter()]$ADApplicationID, # The value should match the value provided for Active Directory Application ID in the Technical Configuration of the Transactable Offer in Partner Center
   [string][Parameter()]$ADApplicationSecret, # Secret key of the AD Application
   [string][Parameter()]$ADApplicationSecretLifetime = "1y", # Generated secret lifetime minus one policy-safety day: positive number followed by m (months) or y (years)
   [string][Parameter()]$ADApplicationIDAdmin, # Multi-Tenant Active Directory Application ID 
   [string][Parameter()]$ADMTApplicationIDPortal, #Multi-Tenant Active Directory Application ID for the Landing Portal
   [switch][Parameter()]$CustomerPortalOrganizationsOnly, # Exclude personal Microsoft accounts from an auto-created Customer Portal app registration
   [string][Parameter()]$IsAdminPortalMultiTenant, # If set to true, the Admin Portal will be configured as a multi-tenant application. This is by default set to false. 
   [string][Parameter()]$SQLDatabaseName, # Name of the database (Defaults to AMPSaaSDB)
   [string][Parameter()]$SQLServerName, # Name of the database server (without database.windows.net)
   [string][Parameter()]$LogoURLpng,  # URL for Publisher .png logo
   [string][Parameter()]$LogoURLico,  # URL for Publisher .ico logo
   [string][Parameter()]$KeyVault, # Name of KeyVault
   [switch][Parameter()]$Quiet #if set, only show error / warning output from script commands
)

# Define the warning message
$message = @"
The SaaS Accelerator is offered under the MIT License as open source software and is not supported by Microsoft.

If you need help with the accelerator or would like to report defects or feature requests use the Issues feature on the GitHub repository at https://aka.ms/SaaSAccelerator

Do you agree? (Y/N)
"@

# Display the message in yellow
Write-Host $message -ForegroundColor Yellow

# Prompt the user for input
$response = Read-Host

# Check the user's response
if ($response -ne 'Y' -and $response -ne 'y') {
    Write-Host "You did not agree. Exiting..." -ForegroundColor Red
    exit
}

# Proceed if the user agrees
Write-Host "Thank you for agreeing. Proceeding with the script..." -ForegroundColor Green

$ErrorActionPreference = "Stop"
$startTime = Get-Date

function Invoke-AzRestJson {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][object]$Body
    )

    $bodyFile = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllText(
            $bodyFile,
            ($Body | ConvertTo-Json -Compress),
            [System.Text.UTF8Encoding]::new($false)
        )
        $response = az rest `
            --method $Method `
            --uri $Uri `
            --headers 'Content-Type=application/json' `
            --body "@$bodyFile" `
            --output json
        if ($LASTEXITCODE -ne 0) {
            Throw "🛑 Azure CLI REST request failed for '$Uri'."
        }
        return $response
    }
    finally {
        Remove-Item -Path $bodyFile -Force -ErrorAction SilentlyContinue
    }
}

#region Select Tenant / Subscription for deployment

$currentContextJson = az account show --output json
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Azure CLI is not authenticated. Run 'az login' and retry."
}
$currentContext = $currentContextJson | ConvertFrom-Json
$currentTenant = $currentContext.tenantId
$currentSubscription = $currentContext.id

$accountsJson = az account list --all --output json
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to list Azure subscriptions available to the current Azure CLI login."
}
$accounts = @($accountsJson | ConvertFrom-Json)

# Get TenantID if not set as argument
if (!($TenantID)) {
    $resourceManagerEndpoint = (az cloud show --query endpoints.resourceManager --output tsv).TrimEnd('/')
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($resourceManagerEndpoint)) {
        Throw "🛑 Failed to determine the Azure Resource Manager endpoint for the current Azure cloud."
    }

    # az rest is a core Azure CLI command and avoids the dynamically installed account extension.
    $tenantsJson = az rest --method get --url "$resourceManagerEndpoint/tenants?api-version=2022-12-01" --output json
    if ($LASTEXITCODE -ne 0) {
        Throw "🛑 Failed to list Azure tenants available to the current Azure CLI login."
    }
    $tenants = @(($tenantsJson | ConvertFrom-Json).value)

    $tenants |
        Select-Object @{ Name = "TenantName"; Expression = { if ($_.displayName) { $_.displayName } else { $_.defaultDomain } } }, TenantId, DefaultDomain |
        Sort-Object TenantName, TenantId |
        Format-Table -AutoSize

    if (!($TenantID = Read-Host "⌨  Type your TenantID or press Enter to accept your current one [$currentTenant]")) {
        $TenantID = $currentTenant
    }
}
else {
    Write-Host "🔑 Tenant provided: $TenantID"
}

$tenantSubscriptions = @($accounts | Where-Object { $_.tenantId -eq $TenantID })
if ($tenantSubscriptions.Count -eq 0) {
    Throw "🛑 No Azure subscriptions are available for tenant '$TenantID'. Run 'az login --tenant $TenantID' and retry."
}

# Get Azure Subscription if not set as argument
if (!($AzureSubscriptionID)) {
    $tenantSubscriptions |
        Select-Object Name, @{ Name = "SubscriptionId"; Expression = { $_.id } }, State, IsDefault |
        Format-Table -AutoSize

    $defaultSubscription = if ($currentTenant -eq $TenantID) {
        $currentSubscription
    }
    else {
        $tenantSubscriptions[0].id
    }

    if (!($AzureSubscriptionID = Read-Host "⌨  Type your SubscriptionID or press Enter to accept [$defaultSubscription]")) {
        $AzureSubscriptionID = $defaultSubscription
    }
}
else {
    Write-Host "🔑 Azure Subscription provided: $AzureSubscriptionID"
}

if (@($tenantSubscriptions | Where-Object { $_.id -eq $AzureSubscriptionID }).Count -ne 1) {
    Throw "🛑 Subscription '$AzureSubscriptionID' is not available in tenant '$TenantID'. Select a subscription listed for the tenant and retry."
}

# Set the Azure CLI context
az account set --subscription $AzureSubscriptionID
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to select Azure subscription '$AzureSubscriptionID'."
}
$selectedContextJson = az account show --output json
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to verify Azure CLI context for subscription '$AzureSubscriptionID'."
}
$selectedContext = $selectedContextJson | ConvertFrom-Json
if ($selectedContext.tenantId -ne $TenantID) {
    Throw "🛑 Subscription '$AzureSubscriptionID' belongs to tenant '$($selectedContext.tenantId)', not selected tenant '$TenantID'."
}
Write-Host "🔑 Azure Subscription '$AzureSubscriptionID' selected."

# Required providers must be registered before the deployment creates resources.
$requiredResourceProviders = @(
    "Microsoft.Resources",
    "Microsoft.Network",
    "Microsoft.Sql",
    "Microsoft.KeyVault",
    "Microsoft.Web"
)
foreach ($providerNamespace in $requiredResourceProviders) {
    $providerState = (az provider show --namespace $providerNamespace --query registrationState --output tsv).Trim()
    if ($LASTEXITCODE -ne 0) {
        Throw "🛑 Failed to query registration state for resource provider '$providerNamespace'."
    }

    if ($providerState -ne "Registered") {
        Write-Host "🔵 Registering resource provider '$providerNamespace' (current state: $providerState)."
        az provider register --namespace $providerNamespace --output none
        if ($LASTEXITCODE -ne 0) {
            Throw "🛑 Failed to request registration for resource provider '$providerNamespace'."
        }

        $providerRegistrationDeadline = (Get-Date).AddMinutes(10)
        do {
            Start-Sleep -Seconds 10
            $providerState = (az provider show --namespace $providerNamespace --query registrationState --output tsv).Trim()
            if ($LASTEXITCODE -ne 0) {
                Throw "🛑 Failed while waiting for resource provider '$providerNamespace' registration."
            }
        } while ($providerState -ne "Registered" -and (Get-Date) -lt $providerRegistrationDeadline)
    }

    if ($providerState -ne "Registered") {
        Throw "🛑 Resource provider '$providerNamespace' did not become Registered within 10 minutes (last state: $providerState)."
    }
    Write-Host "🔑 Resource provider '$providerNamespace' is Registered."
}

#endregion

#region Set up Variables and Default Parameters

if ($ResourceGroupForDeployment -eq "") {
    $ResourceGroupForDeployment = $WebAppNamePrefix 
}
if ($SQLServerName -eq "") {
    $SQLServerName = $WebAppNamePrefix + "-sql"
}
if ($SQLDatabaseName -eq "") {
    $SQLDatabaseName = $WebAppNamePrefix +"AMPSaaSDB"
}

if ($KeyVault -eq "") {
    $KeyVault = $WebAppNamePrefix + "-kv"
}

# Key Vault names are globally unique, so check availability directly instead
# of probing for a resource that is expected not to exist.
$KeyVaultApiUri = "https://management.azure.com/subscriptions/$AzureSubscriptionID/providers/Microsoft.KeyVault/checkNameAvailability?api-version=2019-09-01"
$KeyVaultNameCheck = Invoke-AzRestJson `
    -Method post `
    -Uri $KeyVaultApiUri `
    -Body @{ name = $KeyVault; type = "Microsoft.KeyVault/vaults" } | ConvertFrom-Json

if (!$KeyVaultNameCheck.nameAvailable) {
    Write-Host ""
    Write-Host "🛑 Key Vault name " -NoNewline -ForegroundColor Red
    Write-Host "$KeyVault" -NoNewline -ForegroundColor Red -BackgroundColor Yellow
    Write-Host " is not available: $($KeyVaultNameCheck.message)" -ForegroundColor Red
    Write-Host "   Use a different name with the -KeyVault parameter."
    exit 1
}

$SaaSApiConfiguration_CodeHash= git log --format='%H' -1
$azCliOutput = if($Quiet){'none'} else {'json'}

#endregion

#region Validate Parameters

if($WebAppNamePrefix.Length -gt 21) {
    Throw "🛑 Web name prefix must be less than 21 characters."
    exit 1
}

if(!($WebAppNamePrefix -match "^[a-zA-Z0-9-]+$")) {
    Throw "🛑 Web name prefix only allows alphanumeric characters and hyphens."
}

if(!($KeyVault -match "^[a-zA-Z][a-z0-9-]+$")) {
    Throw "🛑 KeyVault name only allows alphanumeric and hyphens, but cannot start with a number or special character."
    exit 1
}

$ADApplicationSecretLifetime = $ADApplicationSecretLifetime.Trim().ToLowerInvariant()
$credentialLifetimeMatch = [regex]::Match($ADApplicationSecretLifetime, "^(?<Count>[1-9]\d{0,2})(?<Unit>[my])$")
if (!$credentialLifetimeMatch.Success) {
    Throw "🛑 ADApplicationSecretLifetime must be a positive number followed by 'm' for months or 'y' for years, for example: 1m, 2m, 1y, or 2y."
}

$credentialLifetimeCount = [int]$credentialLifetimeMatch.Groups["Count"].Value
$credentialEndDate = [DateTimeOffset]::UtcNow
if ($credentialLifetimeMatch.Groups["Unit"].Value -eq "m") {
    $credentialEndDate = $credentialEndDate.AddMonths($credentialLifetimeCount)
}
else {
    $credentialEndDate = $credentialEndDate.AddYears($credentialLifetimeCount)
}

$credentialEndDate = $credentialEndDate.AddDays(-1)
$credentialEndDateUtc = $credentialEndDate.ToString("yyyy-MM-ddTHH:mm:ss'Z'")


#endregion 

#region pre-checks

# Check if .NET 10 is installed
$dotnetversion = dotnet --version 2>$null
if ($LASTEXITCODE -ne 0 -or !$dotnetversion.StartsWith('10.')) {
    Throw "🛑 .NET 10 SDK not installed. Found SDK version '$dotnetversion'. Install the .NET 10 SDK and re-run the script."
}

# Azure Cloud Shell installs ODBC sqlcmd here, but the directory isn't always in PowerShell's PATH.
if ($IsLinux -and $env:ACC_CLOUD) {
    $env:PATH = "/opt/mssql-tools18/bin:$env:PATH"
}

# Prefer Go sqlcmd, which supports ActiveDirectoryDefault on every platform.
# Azure Cloud Shell includes ODBC sqlcmd, whose access-token file support is Linux-only.
$SqlcmdExecutable = $null
if (!$IsLinux -and !$IsMacOS) {
    $goSqlcmdWindowsPath = Join-Path $env:ProgramFiles "SqlCmd\sqlcmd.exe"
    if (Test-Path $goSqlcmdWindowsPath) {
        $SqlcmdExecutable = $goSqlcmdWindowsPath
    }
}

if (!$SqlcmdExecutable) {
    $sqlcmdCommand = Get-Command sqlcmd -ErrorAction SilentlyContinue
    if ($sqlcmdCommand) {
        $SqlcmdExecutable = $sqlcmdCommand.Source
    }
}

if (!$SqlcmdExecutable) {
    Throw "🛑 sqlcmd not installed. Install Go sqlcmd or mssql-tools18 and re-run the script."
}

$null = & $SqlcmdExecutable --version 2>$null
$UseGoSqlcmd = $LASTEXITCODE -eq 0

if (!$UseGoSqlcmd) {
    $sqlcmdHelp = & $SqlcmdExecutable -? 2>&1
    if ($LASTEXITCODE -ne 0) {
        Throw "🛑 sqlcmd not installed. Install Go sqlcmd or mssql-tools18 and re-run the script."
    }

    if (!$IsLinux -and !$IsMacOS) {
        Throw "🛑 ODBC sqlcmd on Windows cannot use the current Azure CLI access token. Install Go sqlcmd and re-run the script."
    }

    $sqlcmdVersionMatch = [regex]::Match(($sqlcmdHelp -join "`n"), "Version\s+(\d+\.\d+)")
    if (!$sqlcmdVersionMatch.Success -or [version]$sqlcmdVersionMatch.Groups[1].Value -lt [version]"17.8") {
        Throw "🛑 ODBC sqlcmd 17.8 or newer is required for access-token authentication. Install mssql-tools18 or Go sqlcmd and re-run the script."
    }
}

Write-Host "Using sqlcmd: $SqlcmdExecutable"

#endregion


Write-Host "Starting SaaS Accelerator Deployment..."


#region Check SQL Server Name Availability
$SqlServerApiUri = "https://management.azure.com/subscriptions/$AzureSubscriptionID/providers/Microsoft.Sql/checkNameAvailability?api-version=2023-08-01"
$SqlServerNameCheck = Invoke-AzRestJson `
    -Method post `
    -Uri $SqlServerApiUri `
    -Body @{ name = $SQLServerName; type = "Microsoft.Sql/servers" } | ConvertFrom-Json

if (!$SqlServerNameCheck.available) {
    Write-Host ""
    Write-Host "🛑 SQL Server name " -NoNewline -ForegroundColor Red
    Write-Host "$SQLServerName" -NoNewline -ForegroundColor Red -BackgroundColor Yellow
    Write-Host " is not available: $($SqlServerNameCheck.message)" -ForegroundColor Red
    Write-Host "   Use a different name with the -SQLServerName parameter."
    exit 1
}
#endregion

#region Dowloading assets if provided

# Download Publisher's PNG logo
if($LogoURLpng) { 
    Write-Host "📷 Logo image provided"
	Write-Host "   🔵 Downloading Logo image file"
    Invoke-WebRequest -Uri $LogoURLpng -OutFile "../src/CustomerSite/wwwroot/contoso-sales.png"
    Invoke-WebRequest -Uri $LogoURLpng -OutFile "../src/AdminSite/wwwroot/contoso-sales.png"
    Write-Host "   🔵 Logo image downloaded"
}

# Download Publisher's FAVICON logo
if($LogoURLico) { 
    Write-Host "📷 Logo icon provided"
	Write-Host "   🔵 Downloading Logo icon file"
    Invoke-WebRequest -Uri $LogoURLico -OutFile "../src/CustomerSite/wwwroot/favicon.ico"
    Invoke-WebRequest -Uri $LogoURLico -OutFile "../src/AdminSite/wwwroot/favicon.ico"
    Write-Host "   🔵 Logo icon downloaded"
}

#endregion
 
#region Create AAD App Registrations

#Record the current ADApps to reduce deployment instructions at the end
$ISLoginAppProvided = ($ADApplicationIDAdmin -ne "" -or $ADMTApplicationIDPortal -ne "")


if($ISLoginAppProvided){
	Write-Host "🔑 Multi-Tenant App Registrations provided."
	Write-Host "   ➡️ Admin Portal App Registration ID:" $ADApplicationIDAdmin
	Write-Host "   ➡️ Landing Page App Registration ID:" $ADMTApplicationIDPortal
}
else {
	Write-Host "🔑 Multi-Tenant App Registrations not provided."
}



if($IsAdminPortalMultiTenant -eq "true"){
	Write-Host "🔑 Admin Portal App Registration set as Multi-Tenant."
	$IsAdminPortalMultiTenant = $true
}
else {
	Write-Host "🔑 Admin Portal App Registration set as Single-Tenant."
	$IsAdminPortalMultiTenant = $false
}






#Create App Registration for authenticating calls to the Marketplace API
if (!($ADApplicationID)) {   
    $fulfilmentAppRegName = "$WebAppNamePrefix-FulfillmentAppReg"
    Write-Host "🔑 Configure Fulfilment API App Registration"
    try {   
        $existingApplicationsJson = az ad app list --display-name $fulfilmentAppRegName --output json
        if ($LASTEXITCODE -ne 0) {
            Throw "🛑 Failed to check for an existing Fulfilment API app registration."
        }

        $existingApplications = @($existingApplicationsJson | ConvertFrom-Json |
            Where-Object { $_.displayName -eq $fulfilmentAppRegName })
        if ($existingApplications.Count -gt 1) {
            Throw "🛑 Multiple app registrations named '$fulfilmentAppRegName' exist. Run Cleanup.ps1 before retrying the deployment."
        }

        if ($existingApplications.Count -eq 1) {
            $ADApplication = $existingApplications[0]
            Write-Host "   🔵 Reusing existing Fulfilment API App Registration."
        }
        else {
            $ADApplication = az ad app create --only-show-errors --sign-in-audience AzureADMYOrg --display-name $fulfilmentAppRegName | ConvertFrom-Json
            if ($LASTEXITCODE -ne 0 -or !$ADApplication) {
                Throw "🛑 Failed to create the Fulfilment API app registration."
            }
        }

		$ADObjectID = $ADApplication.id
        $ADApplicationID = $ADApplication.appId
        Start-Sleep -Seconds 5 # Give Microsoft Entra ID time to replicate a newly created application.

        $servicePrincipalsJson = az ad sp list --filter "appId eq '$ADApplicationID'" --output json
        if ($LASTEXITCODE -ne 0) {
            Throw "🛑 Failed to check for the Fulfilment API service principal."
        }

        $servicePrincipals = @($servicePrincipalsJson | ConvertFrom-Json)
        if ($servicePrincipals.Count -gt 1) {
            Throw "🛑 Multiple service principals exist for Fulfilment API application '$ADApplicationID'. Resolve the duplicate enterprise applications before retrying."
        }

        if ($servicePrincipals.Count -eq 0) {
            az ad sp create --id $ADApplicationID --output $azCliOutput
            if ($LASTEXITCODE -ne 0) {
                Throw "🛑 Failed to create the Fulfilment API service principal."
            }
        }
        else {
            Write-Host "   🔵 Reusing existing Fulfilment API service principal."
        }

        $ADApplicationSecret = az ad app credential reset --id $ADObjectID --append --display-name 'SaaSAPI' --end-date $credentialEndDateUtc --query password --only-show-errors --output tsv
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($ADApplicationSecret)) {
            Throw "🛑 Failed to create the Fulfilment API credential with lifetime '$ADApplicationSecretLifetime'. Check the tenant's app credential lifetime policy or provide an existing app registration and secret."
        }
				
        Write-Host "   🔵 Fulfilment API App Registration configured."
		Write-Host "      ➡️ Application ID:" $ADApplicationID
    }
    catch [System.Net.WebException],[System.IO.IOException] {
        Write-Host "🚨🚨   $PSItem.Exception"
        break;
    }
}

#Create Multi-Tenant App Registration for Admin Portal User Login
if (!($ADApplicationIDAdmin)) {  
    Write-Host "🔑 Creating Admin Portal SSO App Registration"
    try {
	
		$appCreateRequestBodyJson = @"
{
	"displayName" : "$WebAppNamePrefix-AdminPortalAppReg",
	"api": 
	{
		"requestedAccessTokenVersion" : 2
	},
	"signInAudience" : "AzureADMyOrg",
	"web":
	{ 
		"redirectUris": 
		[
			
			"https://$WebAppNamePrefix-admin.azurewebsites.net",
			"https://$WebAppNamePrefix-admin.azurewebsites.net/",
			"https://$WebAppNamePrefix-admin.azurewebsites.net/Home/Index",
			"https://$WebAppNamePrefix-admin.azurewebsites.net/Home/Index/"
		],
		"logoutUrl": "https://$WebAppNamePrefix-admin.azurewebsites.net/logout",
		"implicitGrantSettings": 
			{ "enableIdTokenIssuance" : true }
	},
	"requiredResourceAccess":
	[{
		"resourceAppId": "00000003-0000-0000-c000-000000000000",
		"resourceAccess":
			[{ 
				"id": "e1fe6dd8-ba31-4d61-89e7-88639da4683d",
				"type": "Scope" 
			}]
	}]
}
"@	
		if ($PsVersionTable.Platform -ne 'Unix') {
			#On Windows, we need to escape quotes and remove new lines before sending the payload to az rest. 
			# See: https://github.com/Azure/azure-cli/blob/dev/doc/quoting-issues-with-powershell.md#double-quotes--are-lost
			$appCreateRequestBodyJson = $appCreateRequestBodyJson.replace('"','\"').replace("`r`n","")
		}

        $adminPortalAppRegName = "$WebAppNamePrefix-AdminPortalAppReg"
        $existingAdminApplicationsJson = az ad app list --display-name $adminPortalAppRegName --output json
        if ($LASTEXITCODE -ne 0) {
            Throw "🛑 Failed to check for an existing Admin Portal SSO app registration."
        }

        $existingAdminApplications = @($existingAdminApplicationsJson | ConvertFrom-Json |
            Where-Object { $_.displayName -eq $adminPortalAppRegName })
        if ($existingAdminApplications.Count -gt 1) {
            Throw "🛑 Multiple app registrations named '$adminPortalAppRegName' exist. Run Cleanup.ps1 before retrying the deployment."
        }

        if ($existingAdminApplications.Count -eq 1) {
            $adminPortalAppReg = $existingAdminApplications[0]
            az rest --method PATCH --headers "Content-Type=application/json" --uri "https://graph.microsoft.com/v1.0/applications/$($adminPortalAppReg.id)" --body $appCreateRequestBodyJson --output none
            if ($LASTEXITCODE -ne 0) {
                Throw "🛑 Failed to update the existing Admin Portal SSO app registration."
            }
            Write-Host "   🔵 Reusing existing Admin Portal SSO App Registration."
        }
        else {
		    $adminPortalAppReg = $(az rest --method POST --headers "Content-Type=application/json" --uri https://graph.microsoft.com/v1.0/applications --body $appCreateRequestBodyJson) | ConvertFrom-Json
            if ($LASTEXITCODE -ne 0 -or !$adminPortalAppReg) {
                Throw "🛑 Failed to create the Admin Portal SSO app registration."
            }
        }
	
		$ADApplicationIDAdmin = $adminPortalAppReg.appId
		$ADMTObjectIDAdmin = $adminPortalAppReg.id
	
        Write-Host "   🔵 Admin Portal SSO App Registration configured."
		Write-Host "      ➡️ Application Id: $ADApplicationIDAdmin"


		# Download Publisher's AppRegistration logo
        if($LogoURLpng) { 
			Write-Host "   🔵 Logo image provided. Setting the Application branding logo"
			Write-Host "      ➡️ Setting the Application branding logo"
			$token=(az account get-access-token --resource "https://graph.microsoft.com" --query accessToken --output tsv)
			$logoWeb = Invoke-WebRequest $LogoURLpng
			$logoContentType = $logoWeb.Headers["Content-Type"]
			$logoContent = $logoWeb.Content
			
			$uploaded = Invoke-WebRequest `
			  -Uri "https://graph.microsoft.com/v1.0/applications/$ADMTObjectIDAdmin/logo" `
			  -Method "PUT" `
			  -Header @{"Authorization"="Bearer $token";"Content-Type"="$logoContentType";} `
			  -Body $logoContent
		    
			Write-Host "      ➡️ Application branding logo set."
        }

    }
    catch [System.Net.WebException],[System.IO.IOException] {
        Write-Host "🚨🚨   $PSItem.Exception"
        break;
    }
}

#Create Multi-Tenant App Registration for Landing Page User Login
$customerPortalSignInAudienceValue = if ($CustomerPortalOrganizationsOnly) {
    "AzureADMultipleOrgs"
}
else {
    "AzureADandPersonalMicrosoftAccount"
}

if (!($ADMTApplicationIDPortal)) {  
    Write-Host "🔑 Creating Landing Page SSO App Registration"
    Write-Host "   ➡️ Sign-in audience: $customerPortalSignInAudienceValue"
    try {
	
		$appCreateRequestBodyJson = @"
{
	"displayName" : "$WebAppNamePrefix-LandingpageAppReg",
	"api": 
	{
		"requestedAccessTokenVersion" : 2
	},
	"signInAudience" : "$customerPortalSignInAudienceValue",
	"web":
	{ 
		"redirectUris": 
		[
			"https://$WebAppNamePrefix-portal.azurewebsites.net",
			"https://$WebAppNamePrefix-portal.azurewebsites.net/",
			"https://$WebAppNamePrefix-portal.azurewebsites.net/Home/Index",
			"https://$WebAppNamePrefix-portal.azurewebsites.net/Home/Index/"
			
		],
		"logoutUrl": "https://$WebAppNamePrefix-portal.azurewebsites.net/logout",
		"implicitGrantSettings": 
			{ "enableIdTokenIssuance" : true }
	},
	"requiredResourceAccess":
	[{
		"resourceAppId": "00000003-0000-0000-c000-000000000000",
		"resourceAccess":
			[{ 
				"id": "e1fe6dd8-ba31-4d61-89e7-88639da4683d",
				"type": "Scope" 
			}]
	}]
}
"@	
		if ($PsVersionTable.Platform -ne 'Unix') {
			#On Windows, we need to escape quotes and remove new lines before sending the payload to az rest. 
			# See: https://github.com/Azure/azure-cli/blob/dev/doc/quoting-issues-with-powershell.md#double-quotes--are-lost
			$appCreateRequestBodyJson = $appCreateRequestBodyJson.replace('"','\"').replace("`r`n","")
		}

        $landingPageAppRegName = "$WebAppNamePrefix-LandingpageAppReg"
        $existingLandingApplicationsJson = az ad app list --display-name $landingPageAppRegName --output json
        if ($LASTEXITCODE -ne 0) {
            Throw "🛑 Failed to check for an existing Landing Page SSO app registration."
        }

        $existingLandingApplications = @($existingLandingApplicationsJson | ConvertFrom-Json |
            Where-Object { $_.displayName -eq $landingPageAppRegName })
        if ($existingLandingApplications.Count -gt 1) {
            Throw "🛑 Multiple app registrations named '$landingPageAppRegName' exist. Run Cleanup.ps1 before retrying the deployment."
        }

        if ($existingLandingApplications.Count -eq 1) {
            $landingpageLoginAppReg = $existingLandingApplications[0]
            az rest --method PATCH --headers "Content-Type=application/json" --uri "https://graph.microsoft.com/v1.0/applications/$($landingpageLoginAppReg.id)" --body $appCreateRequestBodyJson --output none
            if ($LASTEXITCODE -ne 0) {
                Throw "🛑 Failed to update the existing Landing Page SSO app registration."
            }
            Write-Host "   🔵 Reusing existing Landing Page SSO App Registration."
        }
        else {
		    $landingpageLoginAppReg = $(az rest --method POST --headers "Content-Type=application/json" --uri https://graph.microsoft.com/v1.0/applications --body $appCreateRequestBodyJson) | ConvertFrom-Json
            if ($LASTEXITCODE -ne 0 -or !$landingpageLoginAppReg) {
                Throw "🛑 Failed to create the Landing Page SSO app registration with sign-in audience '$customerPortalSignInAudienceValue'. Confirm that tenant policy permits this audience, use -CustomerPortalOrganizationsOnly to exclude personal Microsoft accounts, or supply an existing compatible app registration with -ADMTApplicationIDPortal."
            }
        }
	
		$ADMTApplicationIDPortal = $landingpageLoginAppReg.appId
		$ADMTObjectIDPortal = $landingpageLoginAppReg.id
	
        Write-Host "   🔵 Landing Page SSO App Registration configured."
		Write-Host "      ➡️ Application Id: $ADMTApplicationIDPortal"
	
		# Download Publisher's AppRegistration logo
        if($LogoURLpng) { 
			Write-Host "   🔵 Logo image provided. Setting the Application branding logo"
			Write-Host "      ➡️ Setting the Application branding logo"
			$token=(az account get-access-token --resource "https://graph.microsoft.com" --query accessToken --output tsv)
			$logoWeb = Invoke-WebRequest $LogoURLpng
			$logoContentType = $logoWeb.Headers["Content-Type"]
			$logoContent = $logoWeb.Content
			
			$uploaded = Invoke-WebRequest `
			  -Uri "https://graph.microsoft.com/v1.0/applications/$ADMTObjectIDPortal/logo" `
			  -Method "PUT" `
			  -Header @{"Authorization"="Bearer $token";"Content-Type"="$logoContentType";} `
			  -Body $logoContent
		    
			Write-Host "      ➡️ Application branding logo set."
        }

    }
    catch [System.Net.WebException],[System.IO.IOException] {
        Write-Host "🚨🚨   $PSItem.Exception"
        break;
    }
}

#endregion

#region Prepare Code Packages
Write-host "📜 Prepare publish files for the application"
if (!(Test-Path '../Publish')) {		
	Write-host "   🔵 Preparing Admin Site"  
	dotnet publish ../src/AdminSite/AdminSite.csproj -c release -o ../Publish/AdminSite/ -v q

	Write-host "   🔵 Preparing Metered Scheduler"
	dotnet publish ../src/MeteredTriggerJob/MeteredTriggerJob.csproj -c release -o ../Publish/AdminSite/app_data/jobs/triggered/MeteredTriggerJob/ -v q --runtime win-x64 --self-contained true 

	Write-host "   🔵 Preparing Customer Site"
	dotnet publish ../src/CustomerSite/CustomerSite.csproj -c release -o ../Publish/CustomerSite/ -v q

	Write-host "   🔵 Zipping packages"
	Compress-Archive -Path ../Publish/AdminSite/* -DestinationPath ../Publish/AdminSite.zip -Force
	Compress-Archive -Path ../Publish/CustomerSite/* -DestinationPath ../Publish/CustomerSite.zip -Force
}
#endregion

#region Deploy Azure Resources Infrastructure
Write-host "☁ Deploy Azure Resources"

#Set-up resource name variables
$WebAppNameService=$WebAppNamePrefix+"-asp"
$WebAppNameAdmin=$WebAppNamePrefix+"-admin"
$WebAppNamePortal=$WebAppNamePrefix+"-portal"
$VnetName=$WebAppNamePrefix+"-vnet"
$privateSqlEndpointName=$WebAppNamePrefix+"-db-pe"
$privateKvEndpointName=$WebAppNamePrefix+"-kv-pe"
$privateSqlDnsZoneName="privatelink.database.windows.net"
$privateKvDnsZoneName="privatelink.vaultcore.azure.net"
$privateSqlLink =$WebAppNamePrefix+"-db-link"
$privateKvlink =$WebAppNamePrefix+"-kv-link"
$WebSubnetName="web"
$SqlSubnetName="sql"
$KvSubnetName="kv"
$DefaultSubnetName="default"

#keep the space at the end of the string - bug in az cli running on windows powershell truncates last char https://github.com/Azure/azure-cli/issues/10066
$ADApplicationSecretKeyVault="@Microsoft.KeyVault(VaultName=$KeyVault;SecretName=ADApplicationSecret) "
$DefaultConnectionKeyVault="@Microsoft.KeyVault(VaultName=$KeyVault;SecretName=DefaultConnection) "
$ServerUri = $SQLServerName+".database.windows.net"
$ServerUriPrivate = $SQLServerName+".privatelink.database.windows.net"
$Connection="Server=tcp:"+$ServerUriPrivate+";Database="+$SQLDatabaseName+";TrustServerCertificate=True;Authentication=Active Directory Managed Identity;"

$sqlPublicAccessEnabled = $false
$keyVaultPublicAccessEnabled = $false
$securityControlTagApplied = $false
$securityControlTagExisted = $false
$originalSecurityControlTag = $null
$resourceGroupId = $null
try {

Write-host "   🔵 Resource Group"
Write-host "      ➡️ Create Resource Group"
az group create --location $Location --name $ResourceGroupForDeployment --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to create the resource group."
}

$resourceGroupId = az group show --name $ResourceGroupForDeployment --query id --output tsv
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($resourceGroupId)) {
    Throw "🛑 Failed to retrieve the resource group ID."
}

$originalSecurityControlTag = az group show --name $ResourceGroupForDeployment --query tags.SecurityControl --output tsv
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to inspect the resource group's SecurityControl tag."
}
$securityControlTagExisted = ![string]::IsNullOrWhiteSpace($originalSecurityControlTag)

Write-host "      ➡️ Apply temporary SecurityControl=Ignore tag"
az tag update --resource-id $resourceGroupId --operation Merge --tags SecurityControl=Ignore --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to apply the temporary SecurityControl=Ignore resource group tag."
}
$securityControlTagApplied = $true

Write-host "      ➡️ Create VNET and Subnet"
az network vnet create --resource-group $ResourceGroupForDeployment --name $VnetName --address-prefixes "10.0.0.0/20" --output $azCliOutput
az network vnet subnet create --resource-group $ResourceGroupForDeployment --vnet-name $VnetName -n $DefaultSubnetName --address-prefixes "10.0.0.0/24" --output $azCliOutput
az network vnet subnet create --resource-group $ResourceGroupForDeployment --vnet-name $VnetName -n $WebSubnetName --address-prefixes "10.0.1.0/24" --service-endpoints Microsoft.Sql Microsoft.KeyVault --delegations Microsoft.Web/serverfarms  --output $azCliOutput 
az network vnet subnet create --resource-group $ResourceGroupForDeployment --vnet-name $VnetName -n $SqlSubnetName --address-prefixes "10.0.2.0/24"  --output $azCliOutput 
az network vnet subnet create --resource-group $ResourceGroupForDeployment --vnet-name $VnetName -n $KvSubnetName --address-prefixes "10.0.3.0/24"   --output $azCliOutput 

Write-host "      ➡️ Create Sql Server"
$userId = az ad signed-in-user show --query id -o tsv 
$userdisplayname = az ad signed-in-user show --query displayName -o tsv 
az sql server create --name $SQLServerName --resource-group $ResourceGroupForDeployment --location $Location --enable-ad-only-auth --external-admin-principal-type User --external-admin-name $userdisplayname --external-admin-sid $userId --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to create the SQL Server."
}

$sqlServerId = az sql server show --name $SQLServerName --resource-group $ResourceGroupForDeployment --query id --output tsv
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sqlServerId)) {
    Throw "🛑 Failed to retrieve the SQL Server resource ID."
}

Write-host "      ➡️ Enable temporary SQL public network access"
az resource update --ids $sqlServerId --api-version 2021-11-01 --set properties.publicNetworkAccess=Enabled --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to enable temporary SQL Server public network access. An Azure Policy may prohibit public endpoints."
}

$sqlPublicAccessEnabled = $true

Write-host "      ➡️ Set minimalTlsVersion to 1.2"
az sql server update --name $SQLServerName --resource-group $ResourceGroupForDeployment --set minimalTlsVersion="1.2" --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to configure the SQL Server TLS version."
}

Write-host "      ➡️ Add SQL Server Firewall rules"
az sql server firewall-rule create --resource-group $ResourceGroupForDeployment --server $SQLServerName -n AllowAzureIP --start-ip-address "0.0.0.0" --end-ip-address "0.0.0.0" --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to add the Azure services SQL firewall rule. Confirm that public network access is permitted during deployment."
}

if ($env:ACC_CLOUD -eq $null){
    Write-host "      ➡️ Running in local environment - Add current IP to firewall"
	$publicIp = (Invoke-WebRequest -UseBasicParsing -Uri "https://api.ipify.org").Content
    az sql server firewall-rule create --resource-group $ResourceGroupForDeployment --server $SQLServerName -n AllowIP --start-ip-address "$publicIp" --end-ip-address "$publicIp" --output $azCliOutput
    if ($LASTEXITCODE -ne 0) {
        Throw "🛑 Failed to add the current IP address to the SQL firewall."
    }
}

Write-host "      ➡️ Create SQL DB"
az sql db create --resource-group $ResourceGroupForDeployment --server $SQLServerName --name $SQLDatabaseName  --edition Standard  --capacity 10 --zone-redundant false --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to create the SQL Database."
}

Write-host "   🔵 KeyVault"
Write-host "      ➡️ Create KeyVault"
az keyvault create --name $KeyVault --resource-group $ResourceGroupForDeployment --enable-rbac-authorization false --public-network-access Enabled --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to create Key Vault with temporary public network access required for secret initialization."
}
$keyVaultPublicAccessEnabled = $true

Write-host "      ➡️ Add Secrets"
az keyvault secret set --vault-name $KeyVault --name ADApplicationSecret --value $ADApplicationSecret --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to store the application secret in Key Vault."
}

az keyvault secret set --vault-name $KeyVault --name DefaultConnection --value $Connection --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to store the database connection string in Key Vault."
}

Write-host "      ➡️ Update Firewall"
az keyvault update --name $KeyVault --resource-group $ResourceGroupForDeployment --default-action Deny --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to restrict Key Vault network access."
}

az keyvault network-rule add --name $KeyVault --resource-group $ResourceGroupForDeployment --vnet-name $VnetName --subnet $WebSubnetName --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to allow the web subnet to access Key Vault."
}

Write-host "   🔵 App Service Plan"
Write-host "      ➡️ Create App Service Plan"
az appservice plan create -g $ResourceGroupForDeployment -n $WebAppNameService --sku B1 --output $azCliOutput

Write-host "   🔵 Admin Portal WebApp"
Write-host "      ➡️ Create Web App"
az webapp create -g $ResourceGroupForDeployment -p $WebAppNameService -n $WebAppNameAdmin  --runtime dotnet:10 --output $azCliOutput
Write-host "      ➡️ Assign Identity"
$WebAppNameAdminId = az webapp identity assign -g $ResourceGroupForDeployment  -n $WebAppNameAdmin --identities [system] --query principalId -o tsv
Write-host "      ➡️ Setup access to KeyVault"
az keyvault set-policy --name $KeyVault  --object-id $WebAppNameAdminId --secret-permissions get list --key-permissions get list --resource-group $ResourceGroupForDeployment --output $azCliOutput
Write-host "      ➡️ Set Configuration"
az webapp config connection-string set -g $ResourceGroupForDeployment -n $WebAppNameAdmin -t SQLAzure --output $azCliOutput --settings DefaultConnection=$DefaultConnectionKeyVault 
az webapp config appsettings set -g $ResourceGroupForDeployment  -n $WebAppNameAdmin --output $azCliOutput --settings KnownUsers=$PublisherAdminUsers SaaSApiConfiguration__AdAuthenticationEndPoint=https://login.microsoftonline.com SaaSApiConfiguration__ClientId=$ADApplicationID SaaSApiConfiguration__ClientSecret=$ADApplicationSecretKeyVault SaaSApiConfiguration__FulFillmentAPIBaseURL=https://marketplaceapi.microsoft.com/api SaaSApiConfiguration__FulFillmentAPIVersion=2018-08-31 SaaSApiConfiguration__GrantType=client_credentials SaaSApiConfiguration__MTClientId=$ADApplicationIDAdmin SaaSApiConfiguration__IsAdminPortalMultiTenant=$IsAdminPortalMultiTenant SaaSApiConfiguration__Resource=20e940b3-4c77-4b0b-9a53-9e16a1b010a7 SaaSApiConfiguration__TenantId=$TenantID SaaSApiConfiguration__SignedOutRedirectUri=https://$WebAppNamePrefix-admin.azurewebsites.net/Home/Index/ SaaSApiConfiguration_CodeHash=$SaaSApiConfiguration_CodeHash
az webapp config set -g $ResourceGroupForDeployment -n $WebAppNameAdmin --always-on true  --output $azCliOutput

Write-host "   🔵 Customer Portal WebApp"
Write-host "      ➡️ Create Web App"
az webapp create -g $ResourceGroupForDeployment -p $WebAppNameService -n $WebAppNamePortal --runtime dotnet:10 --output $azCliOutput
Write-host "      ➡️ Assign Identity"
$WebAppNamePortalId= az webapp identity assign -g $ResourceGroupForDeployment  -n $WebAppNamePortal --identities [system] --query principalId -o tsv 
Write-host "      ➡️ Setup access to KeyVault"
az keyvault set-policy --name $KeyVault  --object-id $WebAppNamePortalId --secret-permissions get list --key-permissions get list --resource-group $ResourceGroupForDeployment --output $azCliOutput
Write-host "      ➡️ Set Configuration"
az webapp config connection-string set -g $ResourceGroupForDeployment -n $WebAppNamePortal -t SQLAzure --output $azCliOutput --settings DefaultConnection=$DefaultConnectionKeyVault
az webapp config appsettings set -g $ResourceGroupForDeployment  -n $WebAppNamePortal --output $azCliOutput --settings SaaSApiConfiguration__AdAuthenticationEndPoint=https://login.microsoftonline.com SaaSApiConfiguration__ClientId=$ADApplicationID SaaSApiConfiguration__ClientSecret=$ADApplicationSecretKeyVault SaaSApiConfiguration__FulFillmentAPIBaseURL=https://marketplaceapi.microsoft.com/api SaaSApiConfiguration__FulFillmentAPIVersion=2018-08-31 SaaSApiConfiguration__GrantType=client_credentials SaaSApiConfiguration__MTClientId=$ADMTApplicationIDPortal SaaSApiConfiguration__Resource=20e940b3-4c77-4b0b-9a53-9e16a1b010a7 SaaSApiConfiguration__TenantId=$TenantID SaaSApiConfiguration__SignedOutRedirectUri=https://$WebAppNamePrefix-portal.azurewebsites.net/Home/Index/ SaaSApiConfiguration_CodeHash=$SaaSApiConfiguration_CodeHash
az webapp config set -g $ResourceGroupForDeployment -n $WebAppNamePortal --always-on true --output $azCliOutput

#endregion

#region Deploy Code
Write-host "📜 Deploy Code"

Write-host "   🔵 Deploy Database"
Write-host "      ➡️ Generate SQL schema/data script"
$ConnectionString="Server=tcp:"+$ServerUri+";Database="+$SQLDatabaseName+";Authentication=Active Directory Default;"
Set-Content -Path ../src/AdminSite/appsettings.Development.json -value "{`"ConnectionStrings`": {`"DefaultConnection`":`"$ConnectionString`"}}"
dotnet-ef migrations script  --output script.sql --idempotent --context SaaSKitContext --project ../src/DataAccess/DataAccess.csproj --startup-project ../src/AdminSite/AdminSite.csproj

$sqlTokenFile = $null
try {
    if ($UseGoSqlcmd) {
        $sqlcmdAuthenticationArguments = @("--authentication-method", "ActiveDirectoryDefault")
    }
    else {
        $sqlAccessToken = az account get-access-token --resource https://database.windows.net/ --query accessToken --output tsv
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sqlAccessToken)) {
            Throw "🛑 Unable to acquire an Azure SQL access token."
        }

        # ODBC sqlcmd on Linux requires the access token in a UTF-16LE file without a BOM.
        $sqlTokenFile = [System.IO.Path]::GetTempFileName()
        [System.IO.File]::WriteAllText(
            $sqlTokenFile,
            $sqlAccessToken.Trim(),
            [System.Text.UnicodeEncoding]::new($false, $false)
        )
        $sqlcmdAuthenticationArguments = @("-G", "-P", $sqlTokenFile)
    }

    Write-host "      ➡️ Execute SQL schema/data script"
    & $SqlcmdExecutable -S $ServerUri -d $SQLDatabaseName @sqlcmdAuthenticationArguments -b -l 30 -i ./script.sql
    if ($LASTEXITCODE -ne 0) {
        Throw "🛑 Database migration failed with exit code $LASTEXITCODE."
    }

    Write-host "      ➡️ Execute SQL script to Add WebApps"
    $AddAppsIdsToDB = "CREATE USER [$WebAppNameAdmin] FROM EXTERNAL PROVIDER;ALTER ROLE db_datareader ADD MEMBER  [$WebAppNameAdmin];ALTER ROLE db_datawriter ADD MEMBER  [$WebAppNameAdmin]; GRANT EXEC TO [$WebAppNameAdmin]; CREATE USER [$WebAppNamePortal] FROM EXTERNAL PROVIDER;ALTER ROLE db_datareader ADD MEMBER [$WebAppNamePortal];ALTER ROLE db_datawriter ADD MEMBER [$WebAppNamePortal]; GRANT EXEC TO [$WebAppNamePortal];"
    & $SqlcmdExecutable -S $ServerUri -d $SQLDatabaseName @sqlcmdAuthenticationArguments -b -l 30 -Q $AddAppsIdsToDB
    if ($LASTEXITCODE -ne 0) {
        Throw "🛑 Database role assignment failed with exit code $LASTEXITCODE."
    }
}
finally {
    if ($sqlTokenFile) {
        [System.IO.File]::Delete($sqlTokenFile)
    }
}

Write-host "   🔵 Deploy Code to Admin Portal"
az webapp deploy --resource-group $ResourceGroupForDeployment --name $WebAppNameAdmin --src-path "../Publish/AdminSite.zip" --type zip --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to deploy code to the Admin Portal."
}

Write-host "   🔵 Deploy Code to Customer Portal"
az webapp deploy --resource-group $ResourceGroupForDeployment --name $WebAppNamePortal --src-path "../Publish/CustomerSite.zip" --type zip --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to deploy code to the Customer Portal."
}

Write-host "   🔵 Update Firewall for WebApps and SQL"
az webapp vnet-integration add --resource-group $ResourceGroupForDeployment --name $WebAppNamePortal --vnet $VnetName --subnet $WebSubnetName --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to integrate the Customer Portal with the virtual network."
}

az webapp vnet-integration add --resource-group $ResourceGroupForDeployment --name $WebAppNameAdmin --vnet $VnetName --subnet $WebSubnetName --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to integrate the Admin Portal with the virtual network."
}

az sql server vnet-rule create --name $WebAppNamePrefix-vnet --resource-group $ResourceGroupForDeployment --server $SQLServerName --vnet-name $VnetName --subnet $WebSubnetName --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to allow the web subnet to access SQL Server."
}

Write-host "   🔵 Clean up"
Remove-Item -Path ../src/AdminSite/appsettings.Development.json
Remove-Item -Path script.sql
#Remove-Item -Path ../Publish -recurse -Force

#endregion

#region Create SQL Private Endpoints
# Get SQL Server
$sqlServerId=az sql server show --name $SQLServerName --resource-group $ResourceGroupForDeployment --query id -o tsv
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sqlServerId)) {
    Throw "🛑 Failed to retrieve the SQL Server resource ID."
}

# Create a private endpoint
az network private-endpoint create --name $privateSqlEndpointName --resource-group $ResourceGroupForDeployment --vnet-name $vnetName --subnet $SqlSubnetName --private-connection-resource-id $sqlServerId --group-ids sqlServer --connection-name sqlConnection --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to create the SQL Server private endpoint."
}


# Create a SQL private DNS zone
az network private-dns zone create --name $privateSqlDnsZoneName --resource-group $ResourceGroupForDeployment --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to create the SQL private DNS zone."
}

# Link the SQL private DNS zone to the VNet
az network private-dns link vnet create --name $privateSqlLink --resource-group $ResourceGroupForDeployment --virtual-network $vnetName --zone-name $privateSqlDnsZoneName --registration-enabled false --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to link the SQL private DNS zone to the virtual network."
}

az network private-endpoint dns-zone-group create --resource-group $ResourceGroupForDeployment --endpoint-name $privateSqlEndpointName --name "sql-zone-group"   --private-dns-zone $privateSqlDnsZoneName   --zone-name "sqlserver" --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to configure DNS for the SQL Server private endpoint."
}
#endregion


#region Create KV Private Endpoints
# Get KV Server
$keyVaultId=az keyvault show --name $KeyVault --resource-group $ResourceGroupForDeployment --query id -o tsv
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($keyVaultId)) {
    Throw "🛑 Failed to retrieve the Key Vault resource ID."
}

# Create a KV private endpoint
az network private-endpoint create --name $privateKvEndpointName --resource-group $ResourceGroupForDeployment --vnet-name $vnetName --subnet $KvSubnetName --private-connection-resource-id $keyVaultId --group-ids vault  --connection-name kvConnection --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to create the Key Vault private endpoint."
}


# Create a KV private DNS zone
az network private-dns zone create --name $privateKvDnsZoneName --resource-group $ResourceGroupForDeployment --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to create the Key Vault private DNS zone."
}

# Link the KV private DNS zone to the VNet
az network private-dns link vnet create --name $privateKvLink --resource-group $ResourceGroupForDeployment --virtual-network $vnetName --zone-name $privateKvDnsZoneName --registration-enabled false --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to link the Key Vault private DNS zone to the virtual network."
}

az network private-endpoint dns-zone-group create --resource-group $ResourceGroupForDeployment --endpoint-name $privateKvEndpointName --name "Kv-zone-group"   --private-dns-zone $privateKvDnsZoneName   --zone-name "Kv-zone" --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to configure DNS for the Key Vault private endpoint."
}
#endregion

Write-host "   🔵 Disable temporary public data-plane access"
az resource update --ids $sqlServerId --api-version 2021-11-01 --set properties.publicNetworkAccess=Disabled --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to disable SQL Server public network access."
}
$sqlPublicAccessEnabled = $false

az keyvault update --name $KeyVault --resource-group $ResourceGroupForDeployment --public-network-access Disabled --output $azCliOutput
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to disable Key Vault public network access."
}
$keyVaultPublicAccessEnabled = $false

Write-host "   🔵 Remove temporary SecurityControl tag"
if ($securityControlTagExisted) {
    az tag update --resource-id $resourceGroupId --operation Merge --tags "SecurityControl=$originalSecurityControlTag" --output $azCliOutput
}
else {
    az tag update --resource-id $resourceGroupId --operation Delete --tags SecurityControl --output $azCliOutput
}
if ($LASTEXITCODE -ne 0) {
    Throw "🛑 Failed to restore the resource group's SecurityControl tag."
}
$securityControlTagApplied = $false

}
finally {
    if ($sqlPublicAccessEnabled) {
        az resource update --ids $sqlServerId --api-version 2021-11-01 --set properties.publicNetworkAccess=Disabled --output none
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "SQL Server public network access could not be disabled during failure cleanup."
        }
    }

    if ($keyVaultPublicAccessEnabled) {
        az keyvault update --name $KeyVault --resource-group $ResourceGroupForDeployment --public-network-access Disabled --output none
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Key Vault public network access could not be disabled during failure cleanup."
        }
    }

    if ($securityControlTagApplied) {
        if ($securityControlTagExisted) {
            az tag update --resource-id $resourceGroupId --operation Merge --tags "SecurityControl=$originalSecurityControlTag" --output none
        }
        else {
            az tag update --resource-id $resourceGroupId --operation Delete --tags SecurityControl --output none
        }
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "The temporary SecurityControl resource group tag could not be restored during failure cleanup."
        }
    }
}


#region Present Output

Write-host "✅ If the intallation completed without error complete the folllowing checklist:"
if ($ISLoginAppProvided) {  #If provided then show the user where to add the landing page in AAD, otherwise script did this already for the user.
	Write-host "   🔵 Add The following URLs to the multi-tenant Landing Page AAD App Registration in Azure Portal:"
	Write-host "      ➡️ https://$WebAppNamePrefix-portal.azurewebsites.net"
	Write-host "      ➡️ https://$WebAppNamePrefix-portal.azurewebsites.net/"
	Write-host "      ➡️ https://$WebAppNamePrefix-portal.azurewebsites.net/Home/Index"
	Write-host "      ➡️ https://$WebAppNamePrefix-portal.azurewebsites.net/Home/Index/"
	Write-host "   🔵 Add The following URLs to the multi-tenant Admin Portal AAD App Registration in Azure Portal:"
	Write-host "      ➡️ https://$WebAppNamePrefix-admin.azurewebsites.net"
	Write-host "      ➡️ https://$WebAppNamePrefix-admin.azurewebsites.net/"
	Write-host "      ➡️ https://$WebAppNamePrefix-admin.azurewebsites.net/Home/Index"
	Write-host "      ➡️ https://$WebAppNamePrefix-admin.azurewebsites.net/Home/Index/"
	Write-host "   🔵 Verify ID Tokens checkbox has been checked-out ?"
}

Write-host "   🔵 Add The following URL in PartnerCenter SaaS Technical Configuration"
Write-host "      ➡️ Landing Page section:       https://$WebAppNamePrefix-portal.azurewebsites.net/"
Write-host "      ➡️ Connection Webhook section: https://$WebAppNamePrefix-portal.azurewebsites.net/api/AzureWebhook"
Write-host "      ➡️ Tenant ID:                  $TenantID"
Write-host "      ➡️ AAD Application ID section: $ADApplicationID"
$duration = (Get-Date) - $startTime
Write-Host "Deployment Complete in $($duration.Minutes)m:$($duration.Seconds)s"
Write-Host "DO NOT CLOSE THIS SCREEN.  Please make sure you copy or perform the actions above before closing."
#endregion
