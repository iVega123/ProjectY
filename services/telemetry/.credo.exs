# Credo's defaults, minus one check.
#
# Credo.Check.Readability.ModuleDoc asks for a @moduledoc on every module. None of
# the modules here has one: this is a service, not a library, and nothing
# publishes its docs. The explanation lives in comments beside the code it
# explains, as in the rest of the repository. With the check on, credo reported
# eight issues, all of them this, and could not block anything else.
%{
  configs: [
    %{
      name: "default",
      files: %{included: ["lib/", "test/", "config/"], excluded: []},
      checks: %{
        disabled: [
          {Credo.Check.Readability.ModuleDoc, []}
        ]
      }
    }
  ]
}
