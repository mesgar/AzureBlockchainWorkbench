using System;
using System.Linq;
using System.IO;
using System.Net;
using System.Text;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

using Workbench.Client;
using Workbench.Client.Models;

namespace Workbench.Functions
{
  public static class WorkbenchFunction
  {

    #region CONTRACT SPECIFIC PROPERTIES

    //The applicationID - should be using environment variables for this
    //Environment.GetEnvironmentVariable("WORKBENCH_APPLICATION_ID", EnvironmentVariableTarget.Process);
    static string APPLICATION_ID = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

    //Creating this variable to adhere to the paramter names the API expects
    static string CONTRACT_CODE_ID = "1";

    //The workflowID
    //Environment.GetEnvironmentVariable("WORKBENCH_WORKFLOW_ID", EnvironmentVariableTarget.Process);
    static string WORKFLOW_ID = "2"; // change this to your ID

    static string CONNECTION_ID = "1"; // For Ethereum - this is always 1 in Workbench.

    // your API Endpoint
    //Environment.GetEnvironmentVariable("WORKBENCH_API_ENDPOINT", EnvironmentVariableTarget.Process);
    static string API_URL = "https://abw-umbnhn-api.azurewebsites.net";

    //The ID for the constructor function on the workflow we want to interact with. 
    //this is pulled from /api/v1/applications/{appID}/workflows - and its the contructorID property.
    static string CONTRACT_ID = "5"; // change this to your ID
    static int WORKFLOW_FUNCTION_ID = 13; // change this to your ID

    #endregion

    static HttpClient httpClient => new HttpClient();

    [FunctionName(nameof(WorkbenchFunction))]
    public static async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)]
            HttpRequest req,
        ILogger log)
    {

      try
      {

        log.LogInformation("Getting Auth Token");

        // Make a call to the GetAuthToken method we have defined.
        var authenticationToken = await GetAuthToken(log);



        // Using the .NET Workbench SDK, set the Auth token and the API URL
        // Documentation here: https://github.com/Azure-Samples/blockchain/tree/master/blockchain-workbench/rest-api-samples/dotnet
        GatewayApi.SiteUrl = API_URL;
        GatewayApi.Instance.SetAuthToken(authenticationToken);

        /* 
        * FIRST - Check if use can create or access workbench API's - IE - Do they have a role set? 
        * This will return FALSE the first time you try to run it, because we haven't set the role for our Function yet.
        * Nevertheless we have to call this at least ONCE so that workbench creates the ethereum account for our Azure Function.
        */
        var userInfo = await GatewayApi.Instance.GetCurrentUserDetails();
        
        // first time fails but creates our user
        var canUserAccessWorkbench = await GatewayApi.Instance.CanCurrentUserCreateContractsForWorkflow(WORKFLOW_ID);

        if (!canUserAccessWorkbench)
          return new BadRequestObjectResult("You are not assigned a role in this blockchain account. Please speak with Administrator");

        // Read the request body. For the purposes of this example we'll just create a dynamic object that has the parameters we want
        string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
        dynamic data = JsonConvert.DeserializeObject<object>(requestBody);

        if (string.IsNullOrEmpty(requestBody))
        {
          //return new BadRequestObjectResult("Request Body is Empty");
        }

        /* 
        * Create a new contract using the Workbench SDK
        * All "Actions" have action information, and the functionID we're actually calling is set on the next line.
        */

        var workflowAction = new ActionInformation();

        workflowAction.WorkflowFunctionId = WORKFLOW_FUNCTION_ID;

        /* 
        * Here, we'll set the parameters that our "contructor" function is expecting. 
        * How do we know them? WE CREATED IT IN THE JSON file!
        * Otherwise - you can hit the API at /api/v1/applications/workflows/{workflowId} 
        * and take a look at the CONSTRUCTOR, and what the "name" of the parameters are
        */

        /* THE FOLLOWING IS FOR THE ASSET TRANSFER CONTRACT ON THE WORKBENCH GITHUB PAGE: 
        * https://github.com/Azure-Samples/blockchain/tree/master/blockchain-workbench/application-and-smart-contract-samples/asset-transfer
        */

        /***** NOTE: "PRICE" IS AN INTEGER TYPE AND THAT'S ASSUMED HERE. You will have to add your own type checking *****/
        Int32 unixTimestamp = (Int32)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds;
        string timestamp = unixTimestamp.ToString();
        float temp = 24;
        int humidity = 70;

        workflowAction.WorkflowActionParameters.Add(new WorkflowActionParameter() { Name = "humidity", Value = humidity.ToString() });
        workflowAction.WorkflowActionParameters.Add(new WorkflowActionParameter() { Name = "temperature", Value = temp.ToString() });
        workflowAction.WorkflowActionParameters.Add(new WorkflowActionParameter() { Name = "timestamp", Value = timestamp });


        log.LogInformation(JsonConvert.SerializeObject(workflowAction));

        /* 
        * Tuples FTW :)
        * The SDK returns a success/failure result and also the body of the response. 
        * In this case, the Create endpoint returns the ID of the contract 
        */

//        var (success, newContractID) = await GatewayApi.Instance.CreateNewContractAsync(workflowAction, WORKFLOW_ID, CONTRACT_CODE_ID, CONNECTION_ID);
        var (success, actionResult) = await GatewayApi.Instance.PostWorkflowActionAsync(workflowAction, CONTRACT_ID);

        log.LogInformation($"POST params: \nworkflowID:{WORKFLOW_ID}\nWorkflowFunctionId:{WORKFLOW_FUNCTION_ID}\nContractID:{CONTRACT_ID}");

        log.LogInformation("POST Result: " + success);


        if (success)
          // Lets return the contractID of the new contract that was created!
          return (ActionResult)new OkObjectResult($"Action Result: {actionResult}");
        else
          return new BadRequestObjectResult($"Request Failed - please look at Function logs - error: {actionResult}");

      }
      catch (Exception e)
      {
        return new BadRequestObjectResult(e.Message);
      }

    }


    public static async Task<string> GetAuthToken(ILogger log)
    {

      //This is the ID of your AD Tenant
      //Environment.GetEnvironmentVariable("WORKBENCH_DIRECTORY_ID", EnvironmentVariableTarget.Process);
      var AUTHORITY = "tedorg.onmicrosoft.com";

      //This is the AD registration of the Workbench Gateway API that you already have registered in AD
      //Environment.GetEnvironmentVariable("WORKBENCH_RESOURCE_ID", EnvironmentVariableTarget.Process);
      var RESOURCE_ID = APPLICATION_ID;

      //The Client ID for Web/Api App we registered in the setup instructions
      //Environment.GetEnvironmentVariable("MY_API_CLIENT_ID", EnvironmentVariableTarget.Process);
      var CLIENT_ID = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

      //The Client Secret for this Web/Api registration, so that AD knows we are who we say we are
      //Environment.GetEnvironmentVariable("MY_API_CLIENT_SECRET", EnvironmentVariableTarget.Process);
      var CLIENT_SECRET = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

      AuthenticationContext authContext = new AuthenticationContext($"https://login.windows.net/{AUTHORITY}", true);

      ClientCredential credential = new ClientCredential(CLIENT_ID, CLIENT_SECRET);

      try
      {
        var result = await authContext.AcquireTokenAsync(RESOURCE_ID, credential);
        return result?.AccessToken;
      }
      catch (Exception e)
      {
        log.LogInformation("Exception: " + e.Message);
        return null;
      }
    }
  }
}
