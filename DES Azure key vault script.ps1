# -------------------------------------------------------------------------------- #
    #  Provide the value for the following parameters before executing the script
$subscriptionId = 'fda4b58c-4aad-4b3a-b49c-05c6a78d08da'    
$keyvaultName = 'RPDEV1KeyVault'
    $secretName = 'RPDEV1DataExportSecret'
    $resourceGroupName = 'Group1'
    $location = 'South Central US'
    $connectionString = 'Server=tcp:rpd365test.database.windows.net,1433;Initial Catalog=D365_Test;Persist Security Info=False;User ID=robert.paar@hcl.com@rpd365test;Password={your_password};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
$organizationIdList = '8dabe6e0-2aad-4087-b4a3-2765a2a641f0'
$tenantId = '189de737-c93a-4f5a-8b68-6f4ca9941912'
    # -------------------------------------------------------------------------------- #

# Login to Azure account, select subscription and tenant Id
connect-azaccount -Tenant $tenantId -Subscription $subscriptionId

# Create new resource group if not exists.
$rgAvail = Get-AzureRmResourceGroup -Name $resourceGroupName -Location $location -ErrorAction SilentlyContinue
if(!$rgAvail){
    New-AzureRmResourceGroup -Name $resourceGroupName -Location $location
}

# Create new key vault if not exists.
$kvAvail = Get-AzureRmKeyVault -VaultName $keyvaultName -ResourceGroupName $resourceGroupName -ErrorAction SilentlyContinue
if(!$kvAvail){
    New-AzureRmKeyVault -VaultName $keyvaultName -ResourceGroupName $resourceGroupName -Location $location
    # Wait few seconds for DNS entry to propagate
    Start-Sleep -Seconds 15
}

# Create tags to store allowed set of Organizations.
$secretTags = @{}
foreach ($orgId in $organizationIdList.Split(',')) {
    $secretTags.Add($orgId.Trim(), $tenantId)
}

# Add or update a secret to key vault.
$secretValue = ConvertTo-SecureString $connectionString -AsPlainText -Force
$secret = Set-azKeyVaultSecret -VaultName $keyvaultName -Name $secretName -SecretValue $secretValue -Tags $secretTags

# Authorize application to access key vault.
$servicePrincipal = 'b861dbcc-a7ef-4219-a005-0e4de4ea7dcf'
set-azkeyvaultaccesspolicy -VaultName $keyvaultName -ServicePrincipalName $servicePrincipal -PermissionsToSecrets get

# Display secret url.
Write-Host "Connection key vault URL is "$secret.id.TrimEnd($secret.Version)""