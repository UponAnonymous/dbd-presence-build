using System.Security;

namespace DbdPresence.LocalCi;

/// <summary>The Task Scheduler definition of "DBD Local CI": at the owner's sign-in, as the owner, with highest privileges, hidden, restarted on failure.</summary>
public static class TaskDefinition
{
    /// <summary>The task definition (Task Scheduler XML) for a program path and a user.</summary>
    public static string Build(string programPath, string user)
    {
        var program = SecurityElement.Escape(programPath);
        var owner = SecurityElement.Escape(user);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Runs this repository's GitHub checks on this PC in throwaway environments.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{owner}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Owner">
                  <UserId>{owner}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>true</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>999</Count>
                </RestartOnFailure>
              </Settings>
              <Actions Context="Owner">
                <Exec>
                  <Command>{program}</Command>
                  <Arguments>serve</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
