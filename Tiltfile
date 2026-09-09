# ProjectY local development orchestration.
#
# Kubernetes is the default path. Compose remains available during the migration
# with `tilt up -- --orchestrator=compose`.

load('ext://uibutton', 'cmd_button')

config.define_string('orchestrator', usage = 'kubernetes (default) or compose')
config.define_bool('full', usage = 'Include the complete polyglot topology in Compose mode')
config.define_string_list('resources', args = True)
settings = config.parse()
config.set_enabled_resources(settings.get('resources', []))
orchestrator = settings.get('orchestrator', 'kubernetes')
full = settings.get('full', False)

if orchestrator not in ['kubernetes', 'compose']:
    fail('orchestrator must be `kubernetes` or `compose`')

local_env_exists = os.path.exists('.env')
rabbitmq_definitions_exist = os.path.exists('.rabbitmq-definitions.json')
if local_env_exists != rabbitmq_definitions_exist:
    existing = '.env' if local_env_exists else '.rabbitmq-definitions.json'
    missing = '.rabbitmq-definitions.json' if local_env_exists else '.env'
    fail(
        'Partial local credential set: %s exists but %s is missing. Stop the stack, ' % (existing, missing) +
        'remove volumes initialized with the old credentials, then run ' +
        '`powershell -ExecutionPolicy Bypass -File scripts/New-LocalSecrets.ps1 -Force`.'
    )

missing_local_files = [path for path in ['.env', '.rabbitmq-definitions.json'] if not os.path.exists(path)]
if missing_local_files and config.tilt_subcommand in ['up', 'ci']:
    print('Generating ignored local credentials for the first run...')
    local(
        ['pwsh', '-NoProfile', '-File', 'scripts/New-LocalSecrets.ps1'],
        command_bat = ['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'scripts\\New-LocalSecrets.ps1'],
        echo_off = True,
    )

missing_local_files = [path for path in ['.env', '.rabbitmq-definitions.json'] if not os.path.exists(path)]
if missing_local_files:
    fail('Local credential generation did not create: %s' % ', '.join(missing_local_files))

def build_application_images(target):
    docker_build('projecty/api-gateway:dev', 'services/api-gateway', dockerfile = 'services/api-gateway/Dockerfile', target = target)
    docker_build('projecty/media-guard:dev', 'services/media-guard', dockerfile = 'services/media-guard/Dockerfile', target = target)
    docker_build('projecty/identity:dev', '.', dockerfile = 'services/identity/Dockerfile', target = target)
    docker_build('projecty/rental-core:dev', '.', dockerfile = 'services/rental-core/RentalCore/Dockerfile', target = target)
    docker_build('projecty/billing:dev', '.', dockerfile = 'services/billing/Dockerfile', target = target)
    docker_build('projecty/risk-pricing:dev', '.', dockerfile = 'services/risk-pricing/Dockerfile', target = target)
    docker_build('projecty/telemetry:dev', 'services/telemetry', dockerfile = 'services/telemetry/Dockerfile', target = target)
    docker_build('projecty/console:dev', 'services/console', dockerfile = 'services/console/Dockerfile', target = target)

if orchestrator == 'kubernetes':
    powershell = 'powershell' if os.name == 'nt' else 'pwsh'
    script_prefix = [powershell, '-NoProfile']
    if os.name == 'nt':
        script_prefix += ['-ExecutionPolicy', 'Bypass']

    if config.tilt_subcommand == 'down':
        local(script_prefix + ['-File', 'scripts/kind/Remove-ProjectYCluster.ps1'])
        print('ProjectY Kubernetes environment removed.')
    else:
        local(script_prefix + ['-File', 'scripts/kind/Ensure-ProjectYCluster.ps1'], echo_off = True)
        allow_k8s_contexts('kind-projecty')
        default_registry('localhost:5001')

        build_application_images('final')
        docker_build('projecty/cockroach-schema:dev', 'deploy/db', dockerfile = 'deploy/db/Dockerfile')
        docker_build('projecty/kafka-init:dev', 'deploy/kafka', dockerfile = 'deploy/kafka/Dockerfile')
        docker_build('projecty/cassandra-init:dev', 'deploy/db/cassandra', dockerfile = 'deploy/db/cassandra/Dockerfile')
        docker_build('projecty/schema-init:dev', 'contracts', dockerfile = 'contracts/Dockerfile')

        k8s_yaml(kustomize('deploy/overlays/selfhost'))

        k8s_resource('cockroach-schema', resource_deps = ['cockroachdb'], labels = ['setup'])
        k8s_resource('kafka-topics', resource_deps = ['kafka'], labels = ['setup'])
        k8s_resource('cassandra-schema', resource_deps = ['cassandra'], labels = ['setup'])
        k8s_resource('schema-contracts', resource_deps = ['schema-registry', 'kafka-topics'], labels = ['setup'])
        k8s_resource('identity', resource_deps = ['cockroach-schema', 'schema-contracts', 'media-guard'], labels = ['services'])
        k8s_resource('rental-core', resource_deps = ['cockroach-schema', 'kafka-topics'], labels = ['services'])
        k8s_resource('billing', resource_deps = ['cockroach-schema', 'schema-contracts'], labels = ['services'])
        k8s_resource('risk-pricing', resource_deps = ['schema-contracts'], labels = ['services'])
        k8s_resource('telemetry', resource_deps = ['cassandra-schema', 'kafka-topics'], labels = ['services'])
        k8s_resource('api-gateway', resource_deps = ['identity', 'rental-core'], labels = ['services'])
        k8s_resource('console', resource_deps = ['api-gateway', 'telemetry'], labels = ['services'])
        k8s_resource('projecty', links = [link('http://localhost:8080', 'Console'), link('http://localhost:8080/health/ready', 'Gateway')])

        print('Tilt UI: http://localhost:10350')
        print('Console: http://localhost:8080')
        print('Gateway: http://localhost:8080/health/ready')

if orchestrator == 'compose':
    compose_files = ['docker-compose.yml', 'docker-compose.chaos.yml']
    if full:
        compose_files.append('docker-compose.polyglot.yml')
    docker_compose(compose_files, env_file = '.env', project_name = 'projecty')

    def configure_live_update(image, context, manifests, install_command, build_command = ''):
        update_steps = [
            fall_back_on(context + '/Dockerfile'),
            sync(context, '/workspace'),
            run(install_command, trigger = manifests),
        ]
        if build_command:
            update_steps.append(run(build_command))
        update_steps.append(restart_container())
        docker_build(image, context, dockerfile = context + '/Dockerfile', target = 'development', live_update = update_steps)

    docker_build(
        'projecty/rental-core:dev', '.',
        dockerfile = 'services/rental-core/RentalCore/Dockerfile', target = 'development',
        live_update = [
            fall_back_on(['services/rental-core/RentalCore/Dockerfile', 'services/rental-core/RentalCore/RentalCore.csproj', 'services/risk-pricing/pricing-policy.json']),
            sync('services/rental-core/RentalCore', '/src/services/rental-core/RentalCore'),
            sync('Shared', '/src/Shared'),
            sync('contracts', '/src/contracts'),
            run('dotnet publish /src/services/rental-core/RentalCore/RentalCore.csproj --configuration Release --output /app/publish --no-restore /p:UseAppHost=false'),
            restart_container(),
        ],
    )
    configure_live_update('projecty/api-gateway:dev', 'services/api-gateway', ['services/api-gateway/Cargo.toml', 'services/api-gateway/Cargo.lock'], 'cd /workspace && cargo fetch --locked', 'cd /workspace && cargo build --locked')
    configure_live_update('projecty/media-guard:dev', 'services/media-guard', ['services/media-guard/Cargo.toml', 'services/media-guard/Cargo.lock'], 'cd /workspace && cargo fetch --locked', 'cd /workspace && cargo build --locked')
    docker_build('projecty/identity:dev', '.', dockerfile = 'services/identity/Dockerfile', target = 'development')

    infrastructure = ['toxiproxy', 'cockroachdb', 'redis', 'rabbitmq', 'minio']
    observability = ['tempo', 'loki', 'otel-collector', 'prometheus', 'grafana']
    setup = ['cockroach-init']
    services = ['identity', 'rental-core', 'media-guard']
    if full:
        configure_live_update('projecty/telemetry:dev', 'services/telemetry', ['services/telemetry/mix.exs', 'services/telemetry/mix.lock'], 'cd /workspace && mix deps.get', 'cd /workspace && mix compile')
        configure_live_update('projecty/console:dev', 'services/console', ['services/console/package.json', 'services/console/package-lock.json'], 'cd /workspace && npm ci')
        docker_build('projecty/risk-pricing:dev', '.', dockerfile = 'services/risk-pricing/Dockerfile', target = 'development')
        docker_build('projecty/billing:dev', '.', dockerfile = 'services/billing/Dockerfile', target = 'development')
        infrastructure += ['kafka', 'cassandra', 'schema-registry']
        setup += ['kafka-init', 'cassandra-init', 'schema-init']
        services += ['telemetry', 'risk-pricing', 'console', 'billing']

    for resource in infrastructure:
        dc_resource(resource, labels = ['infra'])
    for resource in observability:
        links = [link('http://localhost:3000', 'Grafana')] if resource == 'grafana' else []
        dc_resource(resource, labels = ['observability'], links = links)
    for resource in setup:
        dc_resource(resource, labels = ['setup'])
    for resource in services:
        dc_resource(resource, labels = ['services'], resource_deps = observability)
    dc_resource('api-gateway', labels = ['services'], links = [link('http://localhost:8090/health/ready', 'Gateway')], resource_deps = observability)

    print('Tilt UI: http://localhost:10350')
    print('Gateway: http://localhost:8090')
    print('Grafana: http://localhost:3000')
    if full:
        print('Console: http://localhost:3001')

    chaos_shell = 'powershell' if os.name == 'nt' else 'pwsh'
    chaos_prefix = [chaos_shell, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'scripts/Invoke-ChaosDrill.ps1']
    for drill in read_json('deploy/chaos/drills.json'):
        cmd_button('chaos-' + drill['id'], resource = 'toxiproxy', argv = chaos_prefix + [drill['id']], text = drill['label'], disabled = not drill['available'])
        cmd_button('chaos-clear-' + drill['id'], resource = 'toxiproxy', argv = chaos_prefix + [drill['id'], '-Clear'], text = 'Clear: ' + drill['label'], disabled = not drill['available'])
