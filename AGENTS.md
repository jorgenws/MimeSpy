## MimeSpy

This is a C# library for detecting MIME types.

# Tech stack
We are using dotnet 10. Tell me if a newer is available.

# Testing
We use XUnit.
We prefer Theory over duplicate setups.
We use NServiceBus over custom stubs where possible.
We use TestContainers for integration tests if necessary.
We use the MTP testing plattform.

## Architecture
This is a one layer library. Don't make it complicated.