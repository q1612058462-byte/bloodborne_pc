/* Minimal online identity and Matching2 lifecycle for bbport's native LAN path.
 *
 * This intentionally does not pretend that room matching is complete.  It
 * moves the game from the offline NP stubs to a deterministic local identity,
 * starts a Matching2 context, and reports the single LAN world.  Room
 * discovery and peer signaling are the next layer.
 */
#include "runtime.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define NP_INVALID_ARGUMENT ((int32_t)0x80550003)
#define MATCHING_INVALID_ARGUMENT ((int32_t)0x80550c03)
#define MATCHING_NOT_INITIALIZED ((int32_t)0x80550c01)
#define MATCHING_INVALID_CONTEXT ((int32_t)0x80550c0a)
#define MATCHING_CONTEXT_STARTED 0x6f02
#define MATCHING_CONTEXT_STOPPED 0x6f03
#define MATCHING_CAUSE_CONTEXT_ACTION 11

typedef struct { char data[16]; int8_t term; int8_t dummy[3]; } NpOnlineId;
typedef struct { NpOnlineId handle; uint8_t opt[8],reserved[8]; } NpId;
typedef void (ABI *NpStateCallback)(int32_t,uint32_t,NpId *,void *);
typedef void (ABI *MatchingContextCallback)(uint16_t,uint16_t,uint8_t,int32_t,void *);
typedef void (ABI *MatchingRequestCallback)(uint16_t,uint32_t,uint16_t,int32_t,const void *,void *);

typedef struct {
    uint64_t pool_size,unknown;
    int32_t priority;
    uint8_t padding[4];
    uint64_t stack_size,size,ssl_pool_size;
} MatchingInit;
typedef struct { MatchingRequestCallback callback; void *arg; uint32_t timeout; uint16_t app_id; uint8_t padding[2]; } MatchingRequestOpt;
typedef struct MatchingWorld {
    struct MatchingWorld *next;
    uint32_t world_id,lobbies,max_lobby_members,lobby_members,rooms,room_members;
    uint8_t pad[3];
} MatchingWorld;
typedef struct { MatchingWorld *world; uint64_t count; } MatchingWorldResponse;

static NpStateCallback state_callback;
static void *state_callback_arg;
static int state_callback_pending;
static int matching_initialized;
static uint16_t matching_context;
static MatchingContextCallback context_callback;
static void *context_callback_arg;
static uint16_t pending_context_event;
static MatchingRequestCallback default_request_callback;
static void *default_request_arg;
static MatchingRequestCallback pending_request_callback;
static void *pending_request_arg;
static uint32_t pending_request_id;
static uint16_t pending_request_event;
static const void *pending_request_data;
static uint32_t next_request=1;
static MatchingWorld world={NULL,1,0,0,0,0,0,{0}};
static MatchingWorldResponse world_response={&world,1};

static int lan_enabled(void) {
    const char *mode=getenv("BB_NETWORK_MODE");
    return mode && (!strcmp(mode,"lan") || !strcmp(mode,"shadnet"));
}

static void fill_online_id(NpOnlineId *out) {
    const char *configured=getenv("BB_LAN_NPID");
    if(!configured || !*configured)configured="Hunter01";
    memset(out,0,sizeof(*out));
    size_t length=strlen(configured); if(length>sizeof(out->data))length=sizeof(out->data);
    memcpy(out->data,configured,length);
}

static ABI int32_t np_get_state(int32_t user,uint32_t *state) {
    (void)user; if(!state)return NP_INVALID_ARGUMENT; *state=2; return 0;
}
static ABI int32_t np_get_online_id(int32_t user,NpOnlineId *id) {
    (void)user; if(!id)return NP_INVALID_ARGUMENT; fill_online_id(id); return 0;
}
static ABI int32_t np_get_np_id(int32_t user,NpId *id) {
    (void)user; if(!id)return NP_INVALID_ARGUMENT; memset(id,0,sizeof(*id));fill_online_id(&id->handle);return 0;
}
static ABI int32_t np_presence(const NpOnlineId *id,int32_t *status) {
    if(!id||!status)return NP_INVALID_ARGUMENT;*status=1;return 0;
}
static ABI int32_t np_register_state(NpStateCallback callback,void *arg) {
    if(!callback)return NP_INVALID_ARGUMENT;state_callback=callback;state_callback_arg=arg;state_callback_pending=1;return 0;
}
static ABI int32_t np_unregister_state(void) { state_callback=NULL;state_callback_arg=NULL;state_callback_pending=0;return 0; }
static ABI int32_t np_check_callback(void) {
    if(state_callback&&state_callback_pending){NpId id;memset(&id,0,sizeof(id));fill_online_id(&id.handle);state_callback_pending=0;state_callback(1,2,&id,state_callback_arg);}
    if(context_callback&&pending_context_event){uint16_t event=pending_context_event;pending_context_event=0;context_callback(matching_context,event,MATCHING_CAUSE_CONTEXT_ACTION,0,context_callback_arg);}
    if(pending_request_callback&&pending_request_event){MatchingRequestCallback callback=pending_request_callback;uint16_t event=pending_request_event;pending_request_callback=NULL;pending_request_event=0;callback(matching_context,pending_request_id,event,0,pending_request_data,pending_request_arg);}
    return 0;
}
static ABI int32_t np_create_async(const void *param) { return param?(int32_t)(0x20000000u+next_request++):NP_INVALID_ARGUMENT; }
static ABI int32_t np_delete_request(int32_t id) { return id>0?0:NP_INVALID_ARGUMENT; }
static ABI int32_t np_poll_async(int32_t id,int32_t *result) { if(id<=0||!result)return NP_INVALID_ARGUMENT;*result=0;return 0; }
static ABI int32_t np_available(int32_t id,const NpOnlineId *online) { return id>0&&online?0:NP_INVALID_ARGUMENT; }
static ABI int32_t np_check_plus(int32_t id,const void *param,uint8_t *result) { if(id<=0||!param||!result)return NP_INVALID_ARGUMENT;*result=1;return 0; }
static ABI int32_t np_parental(int32_t id,const NpOnlineId *online,int8_t *age,void *info) { if(id<=0||!online||!age||!info)return NP_INVALID_ARGUMENT;*age=30;memset(info,0,32);return 0; }

static ABI int32_t matching_initialize(const MatchingInit *param) {
    if(!param || (param->size!=0x28 && param->size!=0x30))return MATCHING_INVALID_ARGUMENT;
    matching_initialized=1;return 0;
}
static ABI int32_t matching_terminate(void) {
    matching_initialized=0;matching_context=0;context_callback=NULL;default_request_callback=NULL;pending_context_event=0;pending_request_callback=NULL;pending_request_event=0;return 0;
}
static ABI int32_t matching_create_context(const void *param,uint16_t *out) {
    if(!matching_initialized)return MATCHING_NOT_INITIALIZED;if(!param||!out)return MATCHING_INVALID_ARGUMENT;
    matching_context=1;*out=matching_context;return 0;
}
static ABI int32_t matching_destroy_context(uint16_t context) {
    if(context!=matching_context||!context)return MATCHING_INVALID_CONTEXT;matching_context=0;return 0;
}
static ABI int32_t matching_register_context(MatchingContextCallback callback,void *arg) {
    if(!matching_initialized)return MATCHING_NOT_INITIALIZED;context_callback=callback;context_callback_arg=arg;return 0;
}
static ABI int32_t matching_context_start(uint16_t context,uint64_t timeout) {
    (void)timeout;if(context!=matching_context||!context)return MATCHING_INVALID_CONTEXT;pending_context_event=MATCHING_CONTEXT_STARTED;return 0;
}
static ABI int32_t matching_context_stop(uint16_t context) {
    if(context!=matching_context||!context)return MATCHING_INVALID_CONTEXT;pending_context_event=MATCHING_CONTEXT_STOPPED;return 0;
}
static ABI int32_t matching_default_request(uint16_t context,const MatchingRequestOpt *opt) {
    if(context!=matching_context||!context)return MATCHING_INVALID_CONTEXT;if(!opt)return MATCHING_INVALID_ARGUMENT;
    default_request_callback=opt->callback;default_request_arg=opt->arg;return 0;
}
static ABI int32_t matching_server_id(uint16_t context,uint16_t *server) {
    if(context!=matching_context||!context)return MATCHING_INVALID_CONTEXT;if(!server)return MATCHING_INVALID_ARGUMENT;*server=1;return 0;
}
static ABI int32_t matching_worlds(uint16_t context,const void *request,const MatchingRequestOpt *opt,uint32_t *request_id) {
    if(context!=matching_context||!context)return MATCHING_INVALID_CONTEXT;if(!request||!request_id)return MATCHING_INVALID_ARGUMENT;
    MatchingRequestCallback callback=opt&&opt->callback?opt->callback:default_request_callback;
    void *arg=opt&&opt->callback?opt->arg:default_request_arg;
    *request_id=next_request++;
    pending_request_callback=callback;pending_request_arg=arg;pending_request_id=*request_id;pending_request_event=0x0002;pending_request_data=&world_response;
    return 0;
}

static const RuntimeExport exports[]={
    {"sceNpGetState",np_get_state},{"sceNpGetOnlineId",np_get_online_id},{"sceNpGetNpId",np_get_np_id},
    {"sceNpGetGamePresenceStatus",np_presence},{"sceNpRegisterStateCallback",np_register_state},
    {"sceNpUnregisterStateCallback",np_unregister_state},{"sceNpCheckCallback",np_check_callback},
    {"sceNpCreateAsyncRequest",np_create_async},{"sceNpDeleteRequest",np_delete_request},
    {"sceNpAbortRequest",np_delete_request},{"sceNpPollAsync",np_poll_async},
    {"sceNpCheckNpAvailability",np_available},{"sceNpCheckPlus",np_check_plus},
    {"sceNpGetParentalControlInfo",np_parental},
    {"sceNpMatching2Initialize",matching_initialize},{"sceNpMatching2Terminate",matching_terminate},
    {"sceNpMatching2CreateContext",matching_create_context},{"sceNpMatching2DestroyContext",matching_destroy_context},
    {"sceNpMatching2RegisterContextCallback",matching_register_context},
    {"sceNpMatching2ContextStart",matching_context_start},{"sceNpMatching2ContextStop",matching_context_stop},
    {"sceNpMatching2SetDefaultRequestOptParam",matching_default_request},
    {"sceNpMatching2GetServerId",matching_server_id},{"sceNpMatching2GetWorldInfoList",matching_worlds}
};

uintptr_t runtime_np_lan_resolve(const char *name) {
    return lan_enabled()?RUNTIME_LOOKUP(exports,name):0;
}
