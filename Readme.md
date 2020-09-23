## About this example
Tests connectivity with System Assigned Managed Identity on client machine to target server.

## Steps to run this Example:
1. With Dockerfile:
    - Provide connection string in Dockerfile "ENTRYPOINT"
    - Run below commands when Docker is running:
    ```bash
    docker image build . --tag msitokentestapp
    docker run msitokentestapp
    ```

2. With dotnet CLI:
    ```bash
    dotnet run "<connection_string_here>"
    ```