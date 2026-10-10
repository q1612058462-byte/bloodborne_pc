#include "runtime.h"
#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

const char *runtime_symbol(const char *name){return name;}
uintptr_t runtime_lookup(const RuntimeExport *table,size_t count,const char *name){for(size_t i=0;i<count;++i)if(!strcmp(table[i].name,name))return(uintptr_t)table[i].function;return 0;}

typedef struct{char data[16];int8_t term,dummy[3];}OnlineId;
typedef struct{OnlineId handle;uint8_t rest[16];}NpId;
typedef void(ABI *StateCallback)(int32_t,uint32_t,NpId*,void*);
typedef void(ABI *ContextCallback)(uint16_t,uint16_t,uint8_t,int32_t,void*);
typedef int32_t(ABI *GetState)(int32_t,uint32_t*);
typedef int32_t(ABI *GetOnline)(int32_t,OnlineId*);
typedef int32_t(ABI *RegisterState)(StateCallback,void*);
typedef int32_t(ABI *CheckCallbacks)(void);
typedef int32_t(ABI *MatchingInit)(const void*);
typedef int32_t(ABI *CreateContext)(const void*,uint16_t*);
typedef int32_t(ABI *RegisterContext)(ContextCallback,void*);
typedef int32_t(ABI *StartContext)(uint16_t,uint64_t);
typedef void(ABI *RequestCallback)(uint16_t,uint32_t,uint16_t,int32_t,const void*,void*);
typedef struct{RequestCallback callback;void*arg;uint32_t timeout;uint16_t app_id;uint8_t padding[2];}RequestOpt;
typedef int32_t(ABI *SetDefaultRequest)(uint16_t,const RequestOpt*);
typedef int32_t(ABI *GetWorlds)(uint16_t,const void*,const RequestOpt*,uint32_t*);

static int state_called,context_called,worlds_called;
static ABI void state_cb(int32_t user,uint32_t state,NpId*id,void*arg){assert(user==1&&state==2&&id&&arg==(void*)1);assert(!strncmp(id->handle.data,"LanHunter",9));state_called++;}
static ABI void context_cb(uint16_t context,uint16_t event,uint8_t cause,int32_t error,void*arg){assert(context==1&&event==0x6f02&&cause==11&&error==0&&arg==(void*)2);context_called++;}
static ABI void worlds_cb(uint16_t context,uint32_t request,uint16_t event,int32_t error,const void*response,void*arg){
    const uint64_t*words=(const uint64_t*)response;assert(context==1&&request&&event==2&&error==0&&response&&arg==(void*)3);assert(words[0]&&words[1]==1);worlds_called++;
}

int main(void){
#ifdef _WIN32
    assert(_putenv_s("BB_NETWORK_MODE","")==0);assert(_putenv_s("BB_LAN_NPID","LanHunter")==0);
#else
    assert(unsetenv("BB_NETWORK_MODE")==0);assert(setenv("BB_LAN_NPID","LanHunter",1)==0);
#endif
    assert(runtime_np_lan_resolve("sceNpGetState")==0);
#ifdef _WIN32
    assert(_putenv_s("BB_NETWORK_MODE","lan")==0);
#else
    assert(setenv("BB_NETWORK_MODE","lan",1)==0);
#endif
    GetState get_state=(GetState)runtime_np_lan_resolve("sceNpGetState");GetOnline get_online=(GetOnline)runtime_np_lan_resolve("sceNpGetOnlineId");
    RegisterState register_state=(RegisterState)runtime_np_lan_resolve("sceNpRegisterStateCallback");CheckCallbacks check=(CheckCallbacks)runtime_np_lan_resolve("sceNpCheckCallback");
    assert(get_state&&get_online&&register_state&&check);uint32_t state=0;OnlineId id={{0},0,{0}};
    assert(get_state(1,&state)==0&&state==2);assert(get_online(1,&id)==0&&!strncmp(id.data,"LanHunter",9));
    assert(register_state(state_cb,(void*)1)==0);assert(check()==0&&state_called==1);
    uint64_t init[6]={0};init[4]=0x30;MatchingInit initialize=(MatchingInit)runtime_np_lan_resolve("sceNpMatching2Initialize");
    CreateContext create=(CreateContext)runtime_np_lan_resolve("sceNpMatching2CreateContext");RegisterContext register_context=(RegisterContext)runtime_np_lan_resolve("sceNpMatching2RegisterContextCallback");StartContext start=(StartContext)runtime_np_lan_resolve("sceNpMatching2ContextStart");
    assert(initialize&&create&&register_context&&start);assert(initialize(init)==0);uint16_t context=0;uint64_t parameter[5]={0};
    assert(create(parameter,&context)==0&&context==1);assert(register_context(context_cb,(void*)2)==0);assert(start(context,1000000)==0);assert(check()==0&&context_called==1);
    SetDefaultRequest set_default=(SetDefaultRequest)runtime_np_lan_resolve("sceNpMatching2SetDefaultRequestOptParam");GetWorlds get_worlds=(GetWorlds)runtime_np_lan_resolve("sceNpMatching2GetWorldInfoList");
    RequestOpt opt={worlds_cb,(void*)3,0,0,{0}};uint16_t server=1;uint32_t request=0;assert(set_default(context,&opt)==0);assert(get_worlds(context,&server,NULL,&request)==0&&request&&worlds_called==0);assert(check()==0&&worlds_called==1);
    puts("PASS: native LAN NP identity and Matching2 lifecycle");return 0;
}
